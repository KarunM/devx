using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

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
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
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
        var response = await _httpClient.PostAsJsonAsync("api/generate", request, _jsonOptions, cancellationToken);
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
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(config.Endpoint.TrimEnd('/') + '/'),
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    public async Task<string> CompleteAsync(string prompt, CancellationToken cancellationToken = default)
    {
        var request = new OpenAiRequest(
            _config.Model,
            [new OpenAiMessage("user", prompt)],
            _config.Temperature,
            _config.MaxTokens);

        var response = await _httpClient.PostAsJsonAsync("v1/chat/completions", request, _jsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OpenAiResponse>(_jsonOptions, cancellationToken);
        if (result?.Choices is not { Count: > 0 } || string.IsNullOrWhiteSpace(result.Choices[0].Message.Content))
        {
            throw new InvalidOperationException("OpenAI returned an empty response.");
        }

        return result.Choices[0].Message.Content;
    }

    public void Dispose() => _httpClient.Dispose();

    private sealed record OpenAiRequest(
        string Model,
        IReadOnlyList<OpenAiMessage> Messages,
        double? Temperature,
        int? MaxTokens);

    private sealed record OpenAiMessage(string Role, string Content);

    private sealed record OpenAiResponse(
        IReadOnlyList<OpenAiChoice> Choices);

    private sealed record OpenAiChoice(OpenAiMessage Message);
}

public static class LlmClientFactory
{
    public static ILlmClient Create(LlmConfig config) => config.Provider.ToLowerInvariant() switch
    {
        "ollama" => new OllamaClient(config),
        "openai" => new OpenAiClient(config),
        _ => throw new NotSupportedException($"LLM provider '{config.Provider}' is not supported.")
    };
}
