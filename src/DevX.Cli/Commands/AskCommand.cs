using System.Text.Json;
using System.Text.RegularExpressions;
using DevX.Application;
using DevX.Domain;
using DevX.Cli;
using DevX.Infrastructure.Storage.SQLite;
using DevX.Indexer;
using DevX.Protocol;
using Microsoft.Data.Sqlite;
using IOFile = System.IO.File;

static partial class Program
{
    static async Task<int> AskAsync(string connectionString, string[] args, string format)
    {
        var useSandbox = args.Contains("--sandbox");
        var traceAsk = HasFlag(args, "--trace-ask") || HasFlag(args, "--debug-ask");
        var useContextWorkflow = HasFlag(args, "--context-workflow");

        // Refuse before anything else -- before reading stdin, before touching the index, and
        // well before any HTTP attempt. `devx ask` with no configured endpoint used to dial
        // http://localhost:8080 and fail slowly with a bare connection error.
        var llmConfig = LoadLlmConfig(FindRepositoryRoot(Environment.CurrentDirectory));
        if (llmConfig is null)
        {
            Console.Error.WriteLine(
                "devx ask is not configured (set llm.endpoint in .devx/config.json)");
            return 1;
        }

        var promptBuilder = new System.Text.StringBuilder();
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] is "--session" or "--format" or "--sandbox" || (i > 1 && args[i - 1] == "--session") || (i > 1 && args[i - 1] == "--format"))
            {
                if (args[i] is "--session" or "--format")
                {
                    i++;
                }
                continue;
            }
            if (promptBuilder.Length > 0) promptBuilder.Append(' ');
            promptBuilder.Append(args[i]);
        }

        var userPrompt = promptBuilder.ToString().Trim();
        if (string.IsNullOrWhiteSpace(userPrompt))
        {
            Console.WriteLine("Enter your prompt (Ctrl+D to finish):");
            userPrompt = await Console.In.ReadToEndAsync();
        }

        if (string.IsNullOrWhiteSpace(userPrompt))
        {
            Console.Error.WriteLine("Prompt is required.");
            return 1;
        }

        var store = new SqliteStorageInitializer(connectionString);
        await store.InitializeAsync();

        if (useContextWorkflow)
        {
            var bootstrap = await AskBootstrap.InitializeAsync(connectionString);
            Console.WriteLine("phase=Bootstrap");
            Console.WriteLine($"index_created={bootstrap.IndexCreated.ToString().ToLowerInvariant()}");
            Console.WriteLine($"session_created={bootstrap.SessionCreated.ToString().ToLowerInvariant()}");
            Console.WriteLine($"session_reused={(!bootstrap.SessionCreated).ToString().ToLowerInvariant()}");
            Console.WriteLine("phase=RepoMap");

            using var contextLlm = LlmClientFactory.Create(llmConfig);
            var contextEngine = new ProtocolEngine(new SearchService(store, (ISymbolStore)store), (ISessionStore)store, store);
            var contextWorkflow = new AskContextWorkflow(contextLlm, contextEngine, traceAsk);
            var contextResult = await contextWorkflow.RunAsync(userPrompt, bootstrap.RepoMap, bootstrap.Session);
            Console.WriteLine();
            if (contextResult is null || !contextResult.Success || contextResult.SourceBlocks is null || contextResult.SourceBlocks.Count == 0)
            {
                var summary = contextResult?.Summary ?? "Context workflow failed.";
                if (!string.IsNullOrWhiteSpace(contextResult?.FailureReason))
                {
                    summary = $"{summary} reason={contextResult.FailureReason}";
                }

                Console.WriteLine($"Completed: {summary}");
                return 1;
            }
            if (!string.IsNullOrWhiteSpace(contextResult.Summary))
            {
                Console.WriteLine($"Completed: {contextResult.Summary}");
            }
            if (!string.IsNullOrWhiteSpace(contextResult.EditProposal))
            {
                Console.WriteLine("Context workflow complete; model produced an edit proposal.");
                Console.WriteLine("Proposal:");
                Console.WriteLine(contextResult.EditProposal);
                return 0;
            }

            var editLlm = LlmClientFactory.Create(llmConfig);
            var editWorkflow = new AskEditWorkflow(editLlm, traceAsk);
            if (traceAsk)
            {
                Console.WriteLine("edit_workflow_start");
            }
            var editResult = await editWorkflow.RunAsync(userPrompt, contextResult, Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "src/DevX.Cli/Program.cs")));
            Console.WriteLine();
            Console.WriteLine($"Completed: {editResult.Summary}");
            return editResult.Success ? 0 : 1;
        }

        var sessionResolution = await ResolveSessionAsync(connectionString, args, allowCreate: true, allowReuse: true);
        var session = sessionResolution.Session;
        if (session is null)
        {
            Console.Error.WriteLine(sessionResolution.ErrorMessage ?? "Session not found.");
            return 1;
        }

        var root = await store.GetRepositoryRootPathAsync(session.RepositoryId);
        var repositoryRoot = root ?? Environment.CurrentDirectory;
        // The session's own repository root wins if it has a config; otherwise keep the config
        // the gate above already resolved. Never invent an endpoint (1.6.11).
        llmConfig = LoadLlmConfig(repositoryRoot) ?? llmConfig;
        llmConfig = ApplyEnvOverrides(llmConfig);

        SandboxManager? sandbox = null;
        IRepositoryStore effectiveStore = store;
        string effectiveRoot = repositoryRoot;

        if (useSandbox)
        {
            sandbox = new SandboxManager(repositoryRoot);
            sandbox.CopySourceTree();
            effectiveStore = new SandboxRepositoryStore(store, sandbox.SandboxRoot);
            effectiveRoot = sandbox.SandboxRoot;
            Console.WriteLine($"Sandbox: {sandbox.SandboxRoot}");
        }

        var launchConfig = CreateServerLaunchConfig(llmConfig);
        using var serverManager = new LlmServerManager(llmConfig, launchConfig);
        try
        {
            await serverManager.EnsureRunningAsync();
        }
        catch (Exception ex)
        {
            WriteAskFailure(format, ex, serverManager.GetDiagnostics(), llmConfig.Endpoint);
            return 1;
        }

        using var llm = LlmClientFactory.Create(llmConfig);
        var engine = new ProtocolEngine(
            new SearchService(effectiveStore, (ISymbolStore)effectiveStore),
            (ISessionStore)effectiveStore,
            effectiveStore);
        var orchestrator = new AskOrchestrator(llm, engine, llmConfig, useSandbox ? sandbox?.SandboxRoot : null);
        var started = DateTimeOffset.UtcNow;

        Console.WriteLine("═══════════════════════════════════════════════════");
        Console.WriteLine("  DevX Ask — LLM-driven code orchestration");
        Console.WriteLine($"  Provider: {llmConfig.Provider} ({llmConfig.Model})");
        if (useSandbox) Console.WriteLine("  Mode: SANDBOX");
        Console.WriteLine("═══════════════════════════════════════════════════");
        Console.WriteLine();
        Console.WriteLine($">>> User: {userPrompt}");
        Console.WriteLine();

        var turnIndex = 0;
        orchestrator.OnTurn += (sender, turn) =>
        {
            turnIndex++;
            if (turn.Role == "assistant" && turn.ToolName == "done")
            {
                Console.WriteLine();
                Console.WriteLine("═══════════════════════════════════════════════════");
                Console.WriteLine($"  DONE: {turn.Content}");
                Console.WriteLine("═══════════════════════════════════════════════════");
            }
            else if (turn.Role == "assistant" && turn.ToolName is not null)
            {
                Console.WriteLine($"\n  [Turn {turnIndex}] 🤖 LLM → Tool call: {turn.ToolName}");
                var thoughtMatch = Regex.Match(turn.Content, @"Thought:\s*(.+?)(?:\n|$)", RegexOptions.Singleline);
                if (thoughtMatch.Success)
                {
                    Console.WriteLine($"             💭 {thoughtMatch.Groups[1].Value.Trim()}");
                }
            }
            else if (turn.Role == "tool")
            {
                if (turn.ToolName == "patch_apply")
                {
                    var success = turn.Content.Contains("\"Success\":true") || turn.Content.Contains("\"success\":true");
                    var status = success ? "✅ PATCH APPLIED" : "❌ PATCH FAILED";
                    Console.WriteLine($"  [Turn {turnIndex}] 🔧 DevX → {status}");
                }
                else if (turn.ToolName == "build")
                {
                    var success = turn.Content.Contains("\"Success\":true") || turn.Content.Contains("\"success\":true");
                    var status = success ? "✅ BUILD PASSED" : "❌ BUILD FAILED";
                    Console.WriteLine($"  [Turn {turnIndex}] 🔧 DevX → {status}");
                }
                else if (turn.ToolName == "test")
                {
                    var success = turn.Content.Contains("\"Success\":true") || turn.Content.Contains("\"success\":true");
                    var status = success ? "✅ TESTS PASSED" : "❌ TESTS FAILED";
                    Console.WriteLine($"  [Turn {turnIndex}] 🔧 DevX → {status}");
                }
                else
                {
                    Console.WriteLine($"  [Turn {turnIndex}] 🔧 DevX → Result for {turn.ToolName} received");
                }
            }
            else if (turn.Role == "assistant")
            {
                var thoughtMatch = Regex.Match(turn.Content, @"Thought:\s*(.+?)(?:\n|$)", RegexOptions.Singleline);
                if (thoughtMatch.Success)
                {
                    Console.WriteLine($"\n  [Turn {turnIndex}] 🤖 LLM → 💭 {thoughtMatch.Groups[1].Value.Trim()}");
                }
            }
        };

        AskResult askResult;
        try
        {
            askResult = await orchestrator.RunAsync(userPrompt, session);
        }
        catch (Exception ex)
        {
            WriteAskFailure(format, ex, serverManager.GetDiagnostics(), llmConfig.Endpoint);
            return 1;
        }
        await store.AddOperationMetricAsync(new OperationMetric("ask", started, DateTimeOffset.UtcNow - started, askResult.Success, session.Id.Value.ToString(), AgentLabel: GetAgentLabel(args)));

        Console.WriteLine();
        Console.WriteLine($"Completed: {askResult.Summary}");

        if (useSandbox && sandbox is not null)
        {
            var changedFiles = sandbox.GetChangedFiles();
            if (changedFiles.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("═══════════════════════════════════════════════════");
                Console.WriteLine("  CHANGED FILES IN SANDBOX");
                Console.WriteLine("═══════════════════════════════════════════════════");
                foreach (var file in changedFiles)
                {
                    Console.WriteLine($"  {file}");
                }

                Console.WriteLine();
                Console.WriteLine("Apply changes to real codebase? [y/N]: ");
                var response = Console.ReadLine()?.Trim().ToLowerInvariant();
                if (response == "y" || response == "yes")
                {
                    sandbox.ApplyChanges();
                    Console.WriteLine("✅ Changes applied to real codebase.");
                }
                else
                {
                    Console.WriteLine("❌ Changes discarded.");
                }
            }
            else
            {
                Console.WriteLine("No files were changed.");
            }
            sandbox.Dispose();
        }

        return askResult.Success ? 0 : 1;
    }

    // `devx ask` needs an LLM endpoint the user actually configured. Nothing else in the CLI
    // does, so this is what decides whether `ask` is advertised -- and whether it is allowed to
    // try at all. Before 1.6.11 an unconfigured repo silently fell back to
    // http://localhost:8080 and the call failed minutes later with a connection error that told
    // the agent nothing; the endpoint is never guessed now.
    static bool IsAskConfigured(string path) => LoadLlmConfig(FindRepositoryRoot(path)) is not null;

    static LlmConfig? LoadLlmConfig(string repositoryRoot)
    {
        var configPath = Path.Combine(repositoryRoot, ".devx", "config.json");
        if (!IOFile.Exists(configPath))
        {
            return null;
        }

        try
        {
            var json = IOFile.ReadAllText(configPath);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("llm", out var llmElement))
            {
                return null;
            }

            var provider = llmElement.GetProperty("provider").GetString() ?? "ollama";
            var model = llmElement.GetProperty("model").GetString() ?? "qwen2.5-coder";
            var endpoint = llmElement.GetProperty("endpoint").GetString() ?? "http://localhost:11434";
            var temperature = 0.7;
            var maxTokens = 8192;
            var contextWindow = 8192;
            var maxToolResultLength = 4000;
            var conversationWindow = 8;
            if (llmElement.TryGetProperty("temperature", out var tempElement) && tempElement.ValueKind == JsonValueKind.Number)
            {
                temperature = tempElement.GetDouble();
            }
            if (llmElement.TryGetProperty("maxTokens", out var maxTokensElement) && maxTokensElement.ValueKind == JsonValueKind.Number)
            {
                maxTokens = maxTokensElement.GetInt32();
            }
            if (llmElement.TryGetProperty("contextWindow", out var cwElement) && cwElement.ValueKind == JsonValueKind.Number)
            {
                contextWindow = cwElement.GetInt32();
            }
            if (llmElement.TryGetProperty("maxToolResultLength", out var mtrlElement) && mtrlElement.ValueKind == JsonValueKind.Number)
            {
                maxToolResultLength = mtrlElement.GetInt32();
            }
            if (llmElement.TryGetProperty("conversationWindow", out var convwElement) && convwElement.ValueKind == JsonValueKind.Number)
            {
                conversationWindow = convwElement.GetInt32();
            }
            return new LlmConfig(provider, model, endpoint, temperature, maxTokens, contextWindow, maxToolResultLength, conversationWindow);
        }
        catch
        {
            return null;
        }
    }

    static LlmConfig ApplyEnvOverrides(LlmConfig config)
    {
        var provider = Environment.GetEnvironmentVariable("OLLAMA_PROVIDER") ?? config.Provider;
        var model = Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? config.Model;
        var endpoint = Environment.GetEnvironmentVariable("OLLAMA_ENDPOINT") ?? config.Endpoint;
        var maxTokensStr = Environment.GetEnvironmentVariable("OLLAMA_MAX_TOKENS");
        var maxTokens = int.TryParse(maxTokensStr, out var parsed) ? parsed : config.MaxTokens;
        var contextWindowStr = Environment.GetEnvironmentVariable("OLLAMA_CONTEXT_WINDOW");
        var contextWindow = int.TryParse(contextWindowStr, out var cwParsed) ? cwParsed : config.ContextWindow;
        var maxToolResultLengthStr = Environment.GetEnvironmentVariable("OLLAMA_MAX_TOOL_RESULT_LENGTH");
        var maxToolResultLength = int.TryParse(maxToolResultLengthStr, out var mtrlParsed) ? mtrlParsed : config.MaxToolResultLength;
        var conversationWindowStr = Environment.GetEnvironmentVariable("OLLAMA_CONVERSATION_WINDOW");
        var conversationWindow = int.TryParse(conversationWindowStr, out var convwParsed) ? convwParsed : config.ConversationWindow;
        return config with { Provider = provider, Model = model, Endpoint = endpoint, MaxTokens = maxTokens, ContextWindow = contextWindow, MaxToolResultLength = maxToolResultLength, ConversationWindow = conversationWindow };
    }

    static LlmServerLaunchConfig CreateServerLaunchConfig(LlmConfig config)
    {
        if (config.Provider.Equals("mlx", StringComparison.OrdinalIgnoreCase) || config.Provider.Equals("mlx-lm", StringComparison.OrdinalIgnoreCase))
        {
            return new LlmServerLaunchConfig(
                "mlx_lm.server",
                ["--model", "mlx-community/Qwen3-8B-4bit", "--port", "8080"],
                "v1/models",
                180,
                1000);
        }

        if (config.Provider.Equals("ollama", StringComparison.OrdinalIgnoreCase))
        {
            return new LlmServerLaunchConfig(
                "ollama",
                ["serve"],
                "api/tags",
                60,
                1000);
        }

        return new LlmServerLaunchConfig(string.Empty, Array.Empty<string>(), "v1/models", 1, 1000);
    }

    static void WriteAskFailure(string format, Exception exception, LlmServerStartupDiagnostics diagnostics, string endpoint, OpenAiMessage? assistantMessage = null, object? usage = null)
    {
        const string safeError = "The LLM request could not be completed. See local server diagnostics for details.";
        if (format == "json")
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Success = false,
                Error = safeError,
                Diagnostics = diagnostics with { FinalFailureReason = safeError },
                AssistantMessage = assistantMessage,
                Usage = usage
            }));
            return;
        }

        Console.Error.WriteLine(safeError);
        Console.Error.WriteLine($"Expected endpoint: {LlmErrorRedaction.SafeEndpoint(endpoint)}");
    }
}
