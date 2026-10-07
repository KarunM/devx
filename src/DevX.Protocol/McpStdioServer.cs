using System.Text.Json;

namespace DevX.Protocol;

/// <summary>Newline-delimited JSON-RPC transport for the local MCP server.</summary>
public sealed class McpStdioServer(McpReferenceServer server)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await input.ReadLineAsync(cancellationToken);
            if (line is null) return;
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException)
            {
                await WriteAsync(output, new { jsonrpc = "2.0", id = (object?)null, error = new { code = -32700, message = "Parse error" } }, cancellationToken);
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                var id = root.TryGetProperty("id", out var requestId) ? requestId.Clone() : (JsonElement?)null;
                if (!root.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
                {
                    await WriteAsync(output, Error(id, -32600, "Invalid Request"), cancellationToken);
                    continue;
                }

                var method = methodElement.GetString();
                if (id is null)
                {
                    // Notifications have no response; initialized is the only required notification.
                    continue;
                }

                var parameters = root.TryGetProperty("params", out var value) ? value : default;
                object? result = method switch
                {
                    "initialize" => await InitializeAsync(parameters, cancellationToken),
                    "ping" => new { },
                    "tools/list" => new { tools = await server.ListToolsAsync(cancellationToken) },
                    "tools/call" => await CallToolAsync(parameters, cancellationToken),
                    _ => null
                };
                if (result is null)
                    await WriteAsync(output, Error(id, -32601, $"Method not found: {method}"), cancellationToken);
                else
                    await WriteAsync(output, new { jsonrpc = "2.0", id, result }, cancellationToken);
            }
        }
    }

    private async Task<object> InitializeAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var requested = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("protocolVersion", out var version)
            ? version.GetString() : null;
        var initialization = await server.InitializeAsync(cancellationToken);
        var negotiated = requested is "2025-06-18" or "2025-03-26" or "2024-11-05" ? requested : initialization.ProtocolVersion;
        return new { protocolVersion = negotiated, capabilities = new { tools = new { } }, serverInfo = new { name = initialization.ServerInfo.Name, version = initialization.ServerInfo.Version } };
    }

    private async Task<object> CallToolAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("name", out var name)
            || !parameters.TryGetProperty("arguments", out var arguments))
            return new { content = new[] { new { type = "text", text = "Missing tool name or arguments." } }, isError = true };
        try
        {
            var response = await server.CallToolAsync(name.GetString() ?? "", arguments, cancellationToken);
            return new { content = new[] { new { type = "text", text = response.Content.GetRawText() } }, isError = response.IsError };
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException or ArgumentException)
        {
            return new { content = new[] { new { type = "text", text = error.Message } }, isError = true };
        }
    }

    private static object Error(JsonElement? id, int code, string message)
        => new { jsonrpc = "2.0", id, error = new { code, message } };

    private static async Task WriteAsync(TextWriter output, object value, CancellationToken cancellationToken)
    {
        await output.WriteLineAsync(JsonSerializer.Serialize(value, JsonOptions).AsMemory(), cancellationToken);
        await output.FlushAsync(cancellationToken);
    }
}
