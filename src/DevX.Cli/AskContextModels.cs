using System.Text.Json;

namespace DevX.Cli;

public sealed record AskContextRequest(string Reason, IReadOnlyList<AskContextRequestItem> Requests);
public sealed record AskContextRequestItem(string Type, string? Query, string? Path, string? Range, string? Id)
{
    public static AskContextRequestItem FromJson(JsonElement item)
    {
        var type = GetString(item, "type") ?? GetString(item, "kind") ?? string.Empty;
        var query = GetString(item, "query") ?? GetString(item, "symbol") ?? GetString(item, "text");
        var path = GetString(item, "path") ?? GetString(item, "file");
        var range = item.TryGetProperty("range", out var rangeProp) ? rangeProp.GetString() : null;
        var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
        if (string.Equals(type, "read_range", StringComparison.OrdinalIgnoreCase))
        {
            type = "read_file";
        }
        return new AskContextRequestItem(type, query, path, range, id);
    }

    private static string? GetString(JsonElement item, string name)
    {
        return item.TryGetProperty(name, out var prop) ? prop.GetString() : null;
    }
}

public static class AskContextRequestParser
{
    public static AskContextRequest Parse(string response)
    {
        using var document = JsonDocument.Parse(response);
        var root = document.RootElement;
        var reason = root.TryGetProperty("reason", out var reasonProp) ? reasonProp.GetString() ?? string.Empty : string.Empty;

        var requests = new List<AskContextRequestItem>();
        if (root.TryGetProperty("requests", out var requestsProp) && requestsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in requestsProp.EnumerateArray())
            {
                requests.Add(AskContextRequestItem.FromJson(item));
            }
        }

        return new AskContextRequest(reason, requests);
    }
}