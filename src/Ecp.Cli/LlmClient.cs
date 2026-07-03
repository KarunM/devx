using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Ecp.Cli;

public interface ILlmClient : IDisposable
{
    Task<string> CompleteAsync(string prompt, CancellationToken cancellationToken = default);
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
        var messages = LlmMessageFormatter.ParseTaggedPrompt(prompt);

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

        var message = result.Choices[0].Message;
        var text = LlmMessageFormatter.FormatAssistantResponse(message);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("LLM returned an empty response.");
        }

        return text;
    }

    public void Dispose() => _httpClient.Dispose();

    private sealed record OpenAiRequest(
        string Model,
        List<OpenAiMessage> Messages,
        bool Stream,
        double? Temperature,
        int? MaxTokens);

    private sealed record OpenAiResponse(
        IReadOnlyList<OpenAiChoice> Choices);

    private sealed record OpenAiChoice(OpenAiMessage Message);
}

public sealed record OpenAiMessage(
    string Role,
    string? Content,
    [property: JsonPropertyName("tool_calls")] List<OpenAiToolCall>? ToolCalls = null);

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
