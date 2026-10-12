using System.Text.Json;

namespace DevX.Cli;

/// <summary>Compact agent-facing notice for content-search coverage that is incomplete or unknown.</summary>
public static class ContentSearchCoverageFormatter
{
    public static string? Format(JsonElement data)
    {
        if (!TryGetPropertyIgnoreCase(data, "contentCoverage", out var coverage) || coverage.ValueKind != JsonValueKind.Object)
            return "Search coverage unknown.";

        var complete = TryGetPropertyIgnoreCase(coverage, "complete", out var completeValue)
            ? completeValue.ValueKind == JsonValueKind.True ? true : completeValue.ValueKind == JsonValueKind.False ? false : (bool?)null
            : null;
        var truncated = TryGetPropertyIgnoreCase(coverage, "outputTruncated", out var truncatedValue) && truncatedValue.ValueKind == JsonValueKind.True;
        var scanCapped = TryGetPropertyIgnoreCase(coverage, "scanCapped", out var cappedValue) && cappedValue.ValueKind == JsonValueKind.True;
        var returned = PropertyText(coverage, "returnedCount");
        var warnings = TryGetPropertyIgnoreCase(coverage, "warnings", out var warningArray) && warningArray.ValueKind == JsonValueKind.Array
            ? string.Join(" ", warningArray.EnumerateArray().Select(item => item.GetString()).Where(item => !string.IsNullOrWhiteSpace(item)))
            : string.Empty;

        if (truncated)
        {
            var note = $"More matches exist; showing {returned} results. Narrow the search.";
            if (warnings.Length > 0)
                note += $" Search coverage note: {warnings}";
            else if (complete is null || scanCapped)
                note += " Search coverage beyond these matches is unknown.";
            return note;
        }
        if (complete is null || scanCapped)
            return warnings.Length == 0 ? "Search coverage unknown; results may be incomplete." : $"Search coverage unknown; results may be incomplete. {warnings}";
        return null;
    }

    private static string PropertyText(JsonElement element, string name)
        => TryGetPropertyIgnoreCase(element, name, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : string.Empty;

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }
}
