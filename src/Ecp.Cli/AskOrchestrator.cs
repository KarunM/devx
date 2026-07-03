using System.Text.Json;
using System.Text.RegularExpressions;
using Ecp.Domain;
using Ecp.Protocol;

namespace Ecp.Cli;

public sealed record AskResult(string Summary, bool Success, IReadOnlyList<AskTurn> TurnHistory);
public sealed record AskTurn(string Role, string Content, string? ToolName = null, string? ToolResult = null);

public sealed class AskOrchestrator
{
    private readonly ILlmClient _llm;
    private readonly IProtocolEngine _protocolEngine;
    private readonly LlmConfig _config;
    private readonly string? _sandboxRoot;

    public event EventHandler<AskTurn>? OnTurn;

    public AskOrchestrator(ILlmClient llm, IProtocolEngine protocolEngine, LlmConfig config, string? sandboxRoot = null)
    {
        _llm = llm;
        _protocolEngine = protocolEngine;
        _config = config;
        _sandboxRoot = sandboxRoot;
    }

    public async Task<AskResult> RunAsync(string userPrompt, SessionState session, CancellationToken cancellationToken = default)
    {
        var turns = new List<AskTurn>();
        var conversation = new List<ConversationMessage>
        {
            new("system", BuildSystemPrompt()),
            new("user", userPrompt)
        };
        var memory = new System.Text.StringBuilder();

        var turnCount = 0;
        const int maxTurns = 30; // Reduced from 50
        var sessionStart = DateTimeOffset.UtcNow;
        var maxSessionDuration = TimeSpan.FromMinutes(10);
        var lastPatchApplyTurn = -1;

        while (turnCount < maxTurns)
        {
            if (DateTimeOffset.UtcNow - sessionStart > maxSessionDuration)
            {
                return new AskResult("Session timed out after 10 minutes. The task may be incomplete.", false, turns);
            }

            turnCount++;
            var prompt = FormatConversationForLlm(conversation, memory);
            var response = await _llm.CompleteAsync(prompt, cancellationToken);

            var toolCall = ParseToolCall(response);
            if (toolCall is null)
            {
                var turn = new AskTurn("assistant", response);
                conversation.Add(new ConversationMessage("assistant", response));
                turns.Add(turn);
                OnTurn?.Invoke(this, turn);
                TrimConversation(conversation, memory, _config.ConversationWindow);
                continue;
            }

            if (toolCall.Tool == "done")
            {
                var summary = SafeGetString(toolCall.Args, "summary") ?? "Task completed.";
                var turn = new AskTurn("assistant", summary, "done");
                turns.Add(turn);
                OnTurn?.Invoke(this, turn);
                return new AskResult(summary, true, turns);
            }

            if (toolCall.Tool == "__json_error")
            {
                var errorMsg = SafeGetString(toolCall.Args, "error") ?? "Invalid JSON";
                var rawMsg = SafeGetString(toolCall.Args, "raw") ?? "";
                var turn = new AskTurn("assistant", response);
                conversation.Add(new ConversationMessage("assistant", response));
                conversation.Add(new ConversationMessage("system", $"Your JSON was malformed: {errorMsg}. Raw: {rawMsg}. Fix and retry with: Action: <tool_call><tool>TOOL_NAME</tool><args>{{\"key\":\"value\"}}</args></tool_call>"));
                turns.Add(turn);
                OnTurn?.Invoke(this, turn);
                TrimConversation(conversation, memory, _config.ConversationWindow);
                continue;
            }

            // Anti-loop: if patch_apply was tried and failed recently, suggest patch_status
            if (toolCall.Tool == "patch_apply" && lastPatchApplyTurn > 0 && turnCount - lastPatchApplyTurn < 5)
            {
                var turn = new AskTurn("assistant", response, toolCall.Tool);
                turns.Add(turn);
                OnTurn?.Invoke(this, turn);
                conversation.Add(new ConversationMessage("assistant", response));
                conversation.Add(new ConversationMessage("system", "You just tried patch_apply and it failed. Use patch_status to see what changed and why."));
                TrimConversation(conversation, memory, _config.ConversationWindow);
                continue;
            }
            if (toolCall.Tool == "patch_apply")
            {
                lastPatchApplyTurn = turnCount;
            }

            var assistantTurn = new AskTurn("assistant", response, toolCall.Tool);
            turns.Add(assistantTurn);
            OnTurn?.Invoke(this, assistantTurn);
            conversation.Add(new ConversationMessage("assistant", response));

            var toolResult = await ExecuteToolAsync(toolCall, session, cancellationToken);
            var toolResultJson = JsonSerializer.Serialize(toolResult);
            var shouldTruncate = toolCall.Tool is "search_symbol" or "search_text" or "callers" or "callees";
            var truncatedResult = shouldTruncate && toolResultJson.Length > _config.MaxToolResultLength
                ? toolResultJson[.._config.MaxToolResultLength] + "\n...[truncated]"
                : toolResultJson;
            var toolTurn = new AskTurn("tool", truncatedResult, toolCall.Tool, truncatedResult);
            turns.Add(toolTurn);
            OnTurn?.Invoke(this, toolTurn);
            conversation.Add(new ConversationMessage("tool", truncatedResult));
            TrimConversation(conversation, memory, _config.ConversationWindow);
        }

        return new AskResult("Reached maximum turn limit. The task may be incomplete.", false, turns);
    }

    private static void TrimConversation(List<ConversationMessage> conversation, System.Text.StringBuilder memory, int window)
    {
        if (conversation.Count <= 2 + window * 2) return;
        var dropStart = 2;
        var dropCount = conversation.Count - 2 - window * 2;
        if (dropCount <= 0) return;

        var sb = new System.Text.StringBuilder();
        sb.Append("Previously: ");
        for (var i = dropStart; i < dropStart + dropCount && i < conversation.Count; i++)
        {
            var msg = conversation[i];
            if (msg.Role == "assistant" && msg.Content.Contains("<tool>"))
            {
                var toolName = ExtractToolName(msg.Content);
                if (!string.IsNullOrEmpty(toolName)) sb.Append($"called {toolName}, ");
            }
        }
        if (sb.Length > 0)
        {
            sb.Length -= 2;
            sb.Append(".");
            if (memory.Length > 0) memory.Append(" ");
            memory.Append(sb.ToString());
        }

        conversation.RemoveRange(dropStart, dropCount);
    }

    private static string? ExtractToolName(string content)
    {
        var match = Regex.Match(content, @"<tool>(\w+)</tool>");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string FormatConversationForLlm(List<ConversationMessage> conversation, System.Text.StringBuilder memory)
    {
        var sb = new System.Text.StringBuilder();
        if (memory.Length > 0)
        {
            sb.Append($"\n<system>\nMemory of earlier turns: {memory}\n</system>\n");
        }
        foreach (var msg in conversation)
        {
            sb.Append($"\n<{msg.Role}>\n{msg.Content}\n</{msg.Role}>\n");
        }
        return sb.ToString();
    }

    private static string BuildSystemPrompt()
    {
        return "You are an expert coding agent. You can ONLY use these exact tools:\n" +
            "search_symbol(query) - search for types, methods, classes by name\n" +
            "search_text(query) - full-text search in files\n" +
            "read_file(query,full?,range?) - read a file. query=path like 'src/Ecp.Cli/Program.cs'. Use range=\"start:end\" to read specific lines.\n" +
            "read_method(query) - read a method by name or id\n" +
            "snapshot(query) - get file structure (types, methods, LOC)\n" +
            "callers(query) - find callers of a method\n" +
            "callees(query) - find callees of a method\n" +
            "patch_preview(version,operations) - VALIDATE a patch before applying. Use this FIRST.\n" +
            "patch_apply(version,operations) - apply code changes. ONLY when user asks to edit.\n" +
            "patch_status() - SHOW what files changed and what errors exist. Use after a failed patch or build.\n" +
            "build() - build the solution\n" +
            "test(filter?) - run tests\n" +
            "done(summary) - finish and report what was done\n\n" +
            "PATCH OPERATIONS:\n" +
            "  InsertText(Path, Content, Position) - insert text. Position='after:42' or 'before:42'\n" +
            "  ReplaceLineRange(Path, Content, Position) - replace lines. Position='start:end' (e.g., '10:15')\n" +
            "  ReplaceMethod(Path, MethodName, ReplacementText) - replace a method body\n" +
            "  InsertMethod(Path, ContainingType, MethodText, Position) - insert method into a class\n" +
            "  AddUsing(Path, Namespace) - add a using directive\n" +
            "  CreateFile(Path, Content) - create a new file\n\n" +
            "PROJECT STRUCTURE:\n" +
            "- src/Ecp.Cli/Program.cs is the MAIN CLI entry. Commands are inline switch cases.\n" +
            "- src/Ecp.Cli/CapabilitiesComposer.cs lists available commands\n" +
            "- src/Ecp.Protocol/ProtocolEngine.cs handles operations\n" +
            "- There is NO separate IndexCommand.cs - index is a case in Program.cs\n\n" +
            "CRITICAL RULES:\n" +
            "1. ALWAYS read files before editing. Use read_file with range to see specific sections.\n" +
            "2. If a path fails, try a DIFFERENT approach. Do not repeat the same path.\n" +
            "3. Use patch_preview BEFORE patch_apply.\n" +
            "4. After patch_apply fails, USE patch_status to see what changed and why.\n" +
            "5. After build fails, USE patch_status to see what changed, then read the error file.\n" +
            "6. After editing, ALWAYS call build() then test() before done().\n" +
            "7. Call done() when complete.\n\n" +
            "OUTPUT FORMAT:\n" +
            "Thought: <your reasoning>\n" +
            "Action: <tool_call><tool>TOOL_NAME</tool><args>{\"key\":\"value\"}</args></tool_call>\n\n" +
            "EXAMPLE - After build fails, use patch_status:\n" +
            "Thought: Build failed. I need to see what changed and what errors exist.\n" +
            "Action: <tool_call><tool>patch_status</tool><args>{}</args></tool_call>\n\n" +
            "IMPORTANT: Only Thought and Action. No extra text. No markdown. Raw XML only.";
    }

    private static ToolCall? ParseToolCall(string response)
    {
        var cleaned = Regex.Replace(response, @"```(?:xml|json)?\s*", string.Empty, RegexOptions.Singleline);
        cleaned = cleaned.Replace("```", string.Empty);
        cleaned = cleaned.Trim();

        var actionMatch = Regex.Match(cleaned, @"Action:\s*(<tool_call>.*?</tool_call>)", RegexOptions.Singleline);
        if (actionMatch.Success)
        {
            cleaned = actionMatch.Groups[1].Value;
        }

        var toolMatch = Regex.Match(cleaned, @"<tool>(\w+)</tool>", RegexOptions.Singleline);
        if (!toolMatch.Success) return null;

        var argsContent = ExtractBalancedJson(cleaned);
        if (argsContent is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(argsContent);
            return new ToolCall(toolMatch.Groups[1].Value, doc.RootElement.Clone());
        }
        catch (JsonException)
        {
            using var errorDoc = JsonDocument.Parse(JsonSerializer.Serialize(new { raw = argsContent, error = "Invalid JSON. Check your braces and quotes." }));
            return new ToolCall("__json_error", errorDoc.RootElement.Clone());
        }
    }

    private static string? ExtractBalancedJson(string text)
    {
        var argsStart = text.IndexOf("<args>", StringComparison.OrdinalIgnoreCase);
        if (argsStart < 0) return null;

        var jsonStart = text.IndexOf('{', argsStart + "<args>".Length);
        if (jsonStart < 0) return null;

        var depth = 0;
        var inString = false;
        var escaped = false;
        var jsonEnd = -1;

        for (var i = jsonStart; i < text.Length; i++)
        {
            var c = text[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (c == '\\')
            {
                escaped = true;
                continue;
            }
            if (c == '"')
            {
                inString = !inString;
                continue;
            }
            if (inString) continue;
            if (c == '{')
            {
                depth++;
                continue;
            }
            if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    jsonEnd = i;
                    break;
                }
            }
        }

        if (jsonEnd < 0) return null;
        return text[jsonStart..(jsonEnd + 1)];
    }

    private async Task<ProtocolResponse> ExecuteToolAsync(ToolCall call, SessionState session, CancellationToken cancellationToken)
    {
        try
        {
            if (call.Tool == "patch_status")
            {
                return await HandlePatchStatusAsync();
            }

            var operation = call.Tool switch
            {
                "search_symbol" => ProtocolOperations.SearchSymbol,
                "search_text" => ProtocolOperations.SearchText,
                "read_file" => "READ_FILE",
                "read_method" => "READ_METHOD",
                "snapshot" => ProtocolOperations.GetFileSnapshot,
                "callers" => ProtocolOperations.GetCallers,
                "callees" => ProtocolOperations.GetCallees,
                "patch_preview" => "PATCH_PREVIEW",
                "patch_apply" => ProtocolOperations.Patch,
                "build" => ProtocolOperations.RunBuild,
                "test" => ProtocolOperations.RunTests,
                _ => throw new NotSupportedException($"Unknown tool: {call.Tool}")
            };

            var payload = BuildPayload(call.Tool, call.Args);
            var request = new ProtocolRequest("1.0", operation, session.Id.Value.ToString(), payload);
            return await _protocolEngine.ExecuteAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            return new ProtocolResponse(
                false,
                JsonSerializer.SerializeToElement(new { error = ex.Message }),
                [],
                [new ProtocolError("ToolExecutionError", $"Failed to execute {call.Tool}: {ex.Message}")]);
        }
    }

    private Task<ProtocolResponse> HandlePatchStatusAsync()
    {
        if (_sandboxRoot is null)
        {
            return Task.FromResult(new ProtocolResponse(
                true,
                JsonSerializer.SerializeToElement(new { message = "Not running in sandbox mode. No patch status available." }),
                [],
                []));
        }

        var changedFiles = new List<string>();
        var diffs = new List<object>();

        foreach (var sandboxFile in EnumerateFiles(_sandboxRoot))
        {
            var relativePath = Path.GetRelativePath(_sandboxRoot, sandboxFile);
            var originalFile = Path.Combine(_sandboxRoot, "..", relativePath); // This is wrong - need actual original root
            // For now, just report what changed
            if (!System.IO.File.Exists(originalFile))
            {
                changedFiles.Add(relativePath + " (new)");
                continue;
            }
            var sandboxContent = System.IO.File.ReadAllText(sandboxFile);
            var originalContent = System.IO.File.ReadAllText(originalFile);
            if (sandboxContent != originalContent)
            {
                changedFiles.Add(relativePath);
            }
        }

        var result = new
        {
            changedFiles = changedFiles,
            message = changedFiles.Count > 0 ? $"{changedFiles.Count} files changed." : "No files changed.",
            suggestion = "If build failed, check that all referenced methods exist and paths are correct."
        };

        return Task.FromResult(new ProtocolResponse(
            true,
            JsonSerializer.SerializeToElement(result),
            [],
            []));
    }

    private static IEnumerable<string> EnumerateFiles(string root)
    {
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".ecp", "bin", "obj", ".vs", ".idea", "node_modules", "dist", "out", "coverage"
        };

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var parts = file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Any(p => ignored.Contains(p))) continue;
            yield return file;
        }
    }

    private static JsonElement BuildPayload(string tool, JsonElement args)
    {
        if (tool == "patch_apply" || tool == "patch_preview")
        {
            return args;
        }

        var builder = new System.Text.Json.Nodes.JsonObject();
        foreach (var prop in args.EnumerateObject())
        {
            builder[prop.Name] = System.Text.Json.Nodes.JsonNode.Parse(prop.Value.GetRawText());
        }

        switch (tool)
        {
            case "search_symbol":
                if (!builder.ContainsKey("query") && builder.ContainsKey("symbol"))
                {
                    builder["query"] = builder["symbol"];
                    builder.Remove("symbol");
                }
                if (!builder.ContainsKey("query") && builder.ContainsKey("name"))
                {
                    builder["query"] = builder["name"];
                    builder.Remove("name");
                }
                break;

            case "read_file":
            case "snapshot":
                if (!builder.ContainsKey("query") && builder.ContainsKey("path"))
                {
                    builder["query"] = builder["path"];
                    builder.Remove("path");
                }
                break;

            case "read_method":
                if (!builder.ContainsKey("query") && !builder.ContainsKey("id") && builder.ContainsKey("name"))
                {
                    builder["query"] = builder["name"];
                    builder.Remove("name");
                }
                break;

            case "callers":
            case "callees":
                if (!builder.ContainsKey("query") && builder.ContainsKey("method"))
                {
                    builder["query"] = builder["method"];
                    builder.Remove("method");
                }
                if (!builder.ContainsKey("query") && builder.ContainsKey("name"))
                {
                    builder["query"] = builder["name"];
                    builder.Remove("name");
                }
                break;
        }

        return JsonSerializer.SerializeToElement(builder);
    }

    private static string? SafeGetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }
        return null;
    }

    private sealed record ConversationMessage(string Role, string Content);
    private sealed record ToolCall(string Tool, JsonElement Args);
}
