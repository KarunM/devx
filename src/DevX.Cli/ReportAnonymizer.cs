using System.Text.Json;
using System.Text.Json.Nodes;

namespace DevX.Cli;

/// <summary>Produces a structurally identical report with identifying labels replaced in first-seen order.</summary>
public static class ReportAnonymizer
{
    public static T Anonymize<T>(T report)
    {
        var node = JsonSerializer.SerializeToNode(report) ?? throw new ArgumentException("Report cannot be null.", nameof(report));
        var labels = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        Visit(node, null, null);
        return node.Deserialize<T>() ?? throw new JsonException("Could not read anonymized report.");

        void Visit(JsonNode? current, string? property, string? parent)
        {
            if (current is JsonObject obj)
            {
                foreach (var pair in obj.ToArray()) Visit(pair.Value, pair.Key, property);
                return;
            }
            if (current is JsonArray array)
            {
                foreach (var item in array.ToArray()) Visit(item, property, parent);
                return;
            }
            if (current is not JsonValue value || !value.TryGetValue<string>(out var text) || text is null) return;
            var key = (property ?? "").ToLowerInvariant();
            if (IsFreeText(key)) { Replace("[redacted]"); return; }
            var category = key == "name" && (parent is "RepositoryActivity" or "Workspaces") ? "repo" : Category(key);
            if (category is not null) Replace(Label(category, text));

            void Replace(string replacement) => current!.ReplaceWith(JsonValue.Create(replacement));
        }

        string Label(string category, string input)
        {
            if (!labels.TryGetValue(category, out var map)) labels[category] = map = new(StringComparer.Ordinal);
            if (!map.TryGetValue(input, out var label)) map[input] = label = $"{category}-{map.Count + 1}";
            return label;
        }
    }

    private static string? Category(string property) => property switch
    {
        "repository" or "repositoryid" or "organization" or "workspace" => "repo",
        "name" or "symbol" or "symbolname" or "qualifiedname" or "attemptedname" or "code" => "symbol",
        "path" or "paths" or "workingset" or "missingpaths" or "stalepaths" => "file",
        "session" or "sessionid" => "session",
        "task" or "tasklabel" => "task",
        "agent" or "agentlabel" or "developer" or "team" => "session",
        _ => null
    };

    private static bool IsFreeText(string property) => property is "metadatajson" or "reason" or "suggestions"
        or "message" or "command" or "failedcommand" or "fallbackaction" or "summary" or "latesttestsummary"
        or "recommendations" or "limitations" or "caveat" or "baseline";
}
