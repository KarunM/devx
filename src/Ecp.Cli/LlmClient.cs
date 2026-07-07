using System.Net.Http.Json;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;

namespace Ecp.Cli;

public interface ILlmClient : IDisposable
{
    Task<string> CompleteAsync(string prompt, CancellationToken cancellationToken = default);
}

public sealed record OpenAiChatMessage(
    string Role,
    string Content);

public static class EcpAskPrompt
{
    public static string BuildSystemPrompt()
    {
        return "You are an ECP-native coding assistant.\n\n" +
            "ECP owns discovery, source reading, patch creation/application, build, test, diagnostics, report, and completion.\n" +
            "The LLM owns requirement understanding, analysis, code-change proposals, and repair suggestions.\n\n" +
            "Return only valid JSON.\n\n" +
            "When ECP asks for analysis, return:\n" +
            "{\n" +
            "  \"kind\": \"analysis\",\n" +
            "  \"understanding\": \"...\",\n" +
            "  \"relevantFiles\": [],\n" +
            "  \"relevantSymbols\": [],\n" +
            "  \"commandDispatchSummary\": \"...\",\n" +
            "  \"likelyEditLocation\": \"...\",\n" +
            "  \"questionsOrMissingContext\": []\n" +
            "}\n\n" +
            "When you need to propose code changes, return a structured change proposal like:\n" +
            "{\n" +
            "  \"kind\": \"change_proposal\",\n" +
            "  \"summary\": \"Add a health command to the CLI.\",\n" +
            "  \"changes\": [\n" +
            "    {\n" +
            "      \"path\": \"src/Ecp.Cli/Program.cs\",\n" +
            "      \"operation\": \"ReplaceMethod\",\n" +
            "      \"target\": \"RunAsync\",\n" +
            "      \"replacement\": \"...\"\n" +
            "    }\n" +
            "  ],\n" +
            "  \"testsToAddOrUpdate\": [],\n" +
            "  \"notes\": []\n" +
            "}\n\n" +
            "Do not describe ECP tool calls.\n" +
            "Do not claim completion.\n" +
            "Do not invent tools.\n\n" +
            "If you cannot proceed safely, return a blocker with the missing context.\n\n" +
            "Final response only:\n" +
            "{\n" +
            "  \"finished\": true,\n" +
            "  \"summary\": \"...\",\n" +
            "  \"filesModified\": [],\n" +
            "  \"filesCreated\": [],\n" +
            "  \"verificationCommands\": [],\n" +
            "  \"remainingLimitations\": []\n" +
            "}";
    }

    public static List<OpenAiMessage> BuildMessages(string userPrompt)
    {
        return new List<OpenAiMessage>
        {
            new("system", BuildSystemPrompt()),
            new("user", userPrompt)
        };
    }
}

public sealed record LlmConfig(
    string Provider,
    string Model,
    string Endpoint,
    double Temperature = 0.7,
    int MaxTokens = 8192,
    int ContextWindow = 8192,
    int MaxToolResultLength = 4000,
    int ConversationWindow = 8);

public sealed record LlmServerLaunchConfig(
    string ServerCommand,
    string[] ServerArguments,
    string ReadyPath = "v1/models",
    int ReadyTimeoutSeconds = 120,
    int ReadyPollMilliseconds = 1000);

public sealed record LlmServerReadinessAttempt(int Attempt, string Url, int? StatusCode, string? Error);

public sealed record LlmServerStartupDiagnostics(
    string ResolvedCommand,
    bool ExecutableExists,
    int? ProcessId,
    string ReadinessUrl,
    IReadOnlyList<LlmServerReadinessAttempt> ReadinessAttempts,
    string? FinalFailureReason);

public sealed class LlmServerStartupException : InvalidOperationException
{
    public LlmServerStartupDiagnostics Diagnostics { get; }

    public LlmServerStartupException(string message, LlmServerStartupDiagnostics diagnostics, Exception? innerException = null)
        : base(message, innerException)
    {
        Diagnostics = diagnostics;
    }
}

public sealed class LlmServerManager : IDisposable
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);

    private readonly LlmConfig _config;
    private readonly LlmServerLaunchConfig _launchConfig;
    private readonly HttpClient _probeClient;
    private readonly List<LlmServerReadinessAttempt> _attempts = new();
    private Process? _process;

    public LlmServerManager(LlmConfig config, LlmServerLaunchConfig launchConfig)
    {
        _config = config;
        _launchConfig = launchConfig;
        _probeClient = new HttpClient
        {
            BaseAddress = new Uri(config.Endpoint.TrimEnd('/') + '/'),
            Timeout = TimeSpan.FromSeconds(5)
        };
    }

    public async Task EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        if (await IsReadyAsync(cancellationToken))
        {
            return;
        }

        var gate = Locks.GetOrAdd(_config.Endpoint, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (await IsReadyAsync(cancellationToken))
            {
                return;
            }

            if (!ExecutableExists())
            {
                throw CreateStartupException($"Executable '{_launchConfig.ServerCommand}' was not found on PATH.");
            }

            StartServer();
            await WaitForReadyAsync(cancellationToken);
        }
        catch
        {
            TryStop();
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    private void StartServer()
    {
        if (_process is { HasExited: false })
        {
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _launchConfig.ServerCommand,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in _launchConfig.ServerArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        _process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start {_launchConfig.ServerCommand}.");
    }

    private async Task WaitForReadyAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(_launchConfig.ReadyTimeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var attemptNumber = _attempts.Count + 1;
            var attempt = await ProbeAsync(attemptNumber, cancellationToken);
            _attempts.Add(attempt);

            if (attempt.StatusCode is >= 200 and < 300)
            {
                return;
            }

            if (_process is { HasExited: true })
            {
                throw CreateStartupException("The LLM server exited before it became ready.");
            }

            await Task.Delay(_launchConfig.ReadyPollMilliseconds, cancellationToken);
        }

        throw CreateStartupException($"The LLM server did not become ready within {_launchConfig.ReadyTimeoutSeconds} seconds.");
    }

    private async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        var attempt = await ProbeAsync(0, cancellationToken);
        if (attempt.Attempt > 0)
        {
            _attempts.Add(attempt);
        }

        return attempt.StatusCode is >= 200 and < 300;
    }

    private async Task<LlmServerReadinessAttempt> ProbeAsync(int attempt, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _probeClient.GetAsync(_launchConfig.ReadyPath, cancellationToken);
            return new LlmServerReadinessAttempt(attempt, _probeClient.BaseAddress + _launchConfig.ReadyPath, (int)response.StatusCode, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new LlmServerReadinessAttempt(attempt, _probeClient.BaseAddress + _launchConfig.ReadyPath, null, ex.Message);
        }
    }

    private void TryStop()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        }
        catch
        {
        }
    }

    private bool ExecutableExists()
    {
        if (Path.IsPathRooted(_launchConfig.ServerCommand))
        {
            return File.Exists(_launchConfig.ServerCommand);
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var segment in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(segment, _launchConfig.ServerCommand);
            if (File.Exists(candidate))
            {
                return true;
            }
        }

        return false;
    }

    private LlmServerStartupException CreateStartupException(string reason)
    {
        var diagnostics = new LlmServerStartupDiagnostics(
            _launchConfig.ServerCommand,
            ExecutableExists(),
            _process?.Id,
            _probeClient.BaseAddress + _launchConfig.ReadyPath,
            _attempts.ToArray(),
            reason);

        return new LlmServerStartupException(reason, diagnostics);
    }

    public LlmServerStartupDiagnostics GetDiagnostics()
    {
        return new LlmServerStartupDiagnostics(
            _launchConfig.ServerCommand,
            ExecutableExists(),
            _process?.Id,
            _probeClient.BaseAddress + _launchConfig.ReadyPath,
            _attempts.ToArray(),
            _process is { HasExited: true } ? "Process exited." : null);
    }

    public void Dispose()
    {
        _probeClient.Dispose();
    }
}

public static class LlmMessageFormatter
{
    private static readonly Regex TaggedMessagePattern = new(
        @"<(system|user|assistant|tool)>\s*([\s\S]*?)\s*</\1>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<OpenAiMessage> ParseTaggedPrompt(string prompt)
    {
        var messages = new List<OpenAiMessage>();
        var matches = TaggedMessagePattern.Matches(prompt);

        if (matches.Count == 0)
        {
            messages.Add(new OpenAiMessage("user", prompt.Trim()));
            return messages;
        }

        foreach (Match match in matches)
        {
            var role = match.Groups[1].Value.ToLowerInvariant();
            var content = match.Groups[2].Value.Trim();
            if (string.IsNullOrEmpty(content))
            {
                continue;
            }

            if (role == "tool")
            {
                messages.Add(new OpenAiMessage("user", $"Tool result:\n{content}"));
                continue;
            }

            messages.Add(new OpenAiMessage(role, content));
        }

        return messages;
    }

    public static string FormatAssistantResponse(OpenAiMessage message)
    {
        if (message.ToolCalls is { Count: > 0 })
        {
            return FormatToolCalls(message.ToolCalls);
        }

        return StripThinking(message.Content ?? string.Empty);
    }

    public static string FormatToolCalls(IReadOnlyList<OpenAiToolCall> toolCalls)
    {
        var sb = new StringBuilder();
        foreach (var toolCall in toolCalls)
        {
            using var argsDoc = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(toolCall.Function.Arguments) ? "{}" : toolCall.Function.Arguments);
            var payload = JsonSerializer.Serialize(new
            {
                name = toolCall.Function.Name,
                arguments = argsDoc.RootElement.Clone()
            });

            sb.AppendLine("<tool_call>");
            sb.AppendLine(payload);
            sb.AppendLine("</tool_call>");
        }

        return sb.ToString().TrimEnd();
    }

    public static string StripThinking(string text)
    {
        var thinkEnd = text.LastIndexOf("</think>", StringComparison.Ordinal);
        if (thinkEnd >= 0)
        {
            text = text[(thinkEnd + "</think>".Length)..];
        }

        return text.Trim();
    }
}

public sealed class OllamaClient : ILlmClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly LlmConfig _config;
    private readonly JsonSerializerOptions _jsonOptions;

    public OllamaClient(LlmConfig config)
    {
        _config = config;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(config.Endpoint.TrimEnd('/') + '/'),
            Timeout = TimeSpan.FromSeconds(300)
        };
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    public async Task<string> CompleteAsync(string prompt, CancellationToken cancellationToken = default)
    {
        var request = new OllamaRequest(_config.Model, prompt, false, _config.Temperature, _config.MaxTokens);
        var json = JsonSerializer.Serialize(request, _jsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync("api/generate", content, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OllamaResponse>(_jsonOptions, cancellationToken);
        if (result is null || string.IsNullOrWhiteSpace(result.Response))
        {
            throw new InvalidOperationException("Ollama returned an empty response.");
        }

        return result.Response;
    }

    public void Dispose() => _httpClient.Dispose();

    private sealed record OllamaRequest(
        string Model,
        string Prompt,
        bool Stream,
        double? Temperature,
        int? NumPredict);

    private sealed record OllamaResponse(
        string Response,
        bool Done);
}

public sealed class OpenAiClient : ILlmClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly LlmConfig _config;
    private readonly JsonSerializerOptions _jsonOptions;

    public OpenAiClient(LlmConfig config)
    {
        _config = config;
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 1,
            PooledConnectionLifetime = TimeSpan.Zero,
        };
        _httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(config.Endpoint.TrimEnd('/') + '/'),
            Timeout = TimeSpan.FromSeconds(300)
        };
        _httpClient.DefaultRequestVersion = new Version(1, 0);
        _httpClient.DefaultRequestHeaders.Add("Connection", "close");
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    public async Task<string> CompleteAsync(string prompt, CancellationToken cancellationToken = default)
    {
        var completion = await CompleteWithMetadataAsync(prompt, cancellationToken);
        return completion.Text;
    }

    public async Task<OpenAiCompletionResult> CompleteWithMetadataAsync(string prompt, CancellationToken cancellationToken = default)
    {
        var messages = LlmMessageFormatter.ParseTaggedPrompt(prompt);
        if (messages.Count == 1 && messages[0].Role == "user")
        {
            messages.Insert(0, new OpenAiMessage("system", EcpAskPrompt.BuildSystemPrompt()));
        }

        var request = new OpenAiRequest(
            _config.Model,
            messages,
            false,
            _config.Temperature,
            _config.MaxTokens);

        var json = JsonSerializer.Serialize(request, _jsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        content.Headers.ContentLength = json.Length;

        var response = await _httpClient.PostAsync("v1/chat/completions", content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"LLM request failed ({(int)response.StatusCode}): {errorBody}");
        }

        var result = await response.Content.ReadFromJsonAsync<OpenAiResponse>(_jsonOptions, cancellationToken);
        if (result?.Choices is not { Count: > 0 })
        {
            throw new InvalidOperationException("LLM returned no choices.");
        }

        var choice = result.Choices[0];
        var text = LlmMessageFormatter.FormatAssistantResponse(choice.Message);
        return new OpenAiCompletionResult(text, choice.Message, result.Usage);
    }

    public void Dispose() => _httpClient.Dispose();

    private sealed record OpenAiRequest(
        string Model,
        List<OpenAiMessage> Messages,
        bool Stream,
        double? Temperature,
        int? MaxTokens);

    private sealed record OpenAiResponse(
        IReadOnlyList<OpenAiChoice> Choices,
        OpenAiUsage? Usage = null);

    private sealed record OpenAiChoice(OpenAiMessage Message);

    private sealed record OpenAiUsage(
        [property: JsonPropertyName("prompt_tokens")] int? PromptTokens,
        [property: JsonPropertyName("completion_tokens")] int? CompletionTokens,
        [property: JsonPropertyName("total_tokens")] int? TotalTokens);
}

public sealed record OpenAiCompletionResult(
    string Text,
    OpenAiMessage AssistantMessage,
    object? Usage);

public sealed record OpenAiMessage(
    string Role,
    string? Content,
    [property: JsonPropertyName("tool_calls")] List<OpenAiToolCall>? ToolCalls = null,
    [property: JsonPropertyName("reasoning")] string? Reasoning = null);

public sealed record OpenAiToolCall(OpenAiFunction Function);

public sealed record OpenAiFunction(string Name, string Arguments);

public static class LlmClientFactory
{
    public static ILlmClient Create(LlmConfig config) => config.Provider.ToLowerInvariant() switch
    {
        "ollama" => new OllamaClient(config),
        "openai" or "mlx" or "mlx-lm" or "openai-compatible" => new OpenAiClient(config),
        _ => throw new NotSupportedException(
            $"LLM provider '{config.Provider}' is not supported. Use ollama, openai, mlx, or mlx-lm.")
    };
}
