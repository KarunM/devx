using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DevX.Domain;

namespace DevX.Protocol;

/// <summary>
/// Text-backed `references` / `callers` for languages without a resolved call graph (Python).
/// A whole-word search for the simple name over indexed files, each hit classified and attributed
/// to the innermost indexed symbol that contains its line. It is a usage search by name, not a
/// resolved graph: the response says so.
/// </summary>
public static class TextReferences
{
    public const int Cap = 50;
    /// <summary>`callers` with more call sites than this lists one line per enclosing symbol unless `--full` is passed.</summary>
    public const int GroupThreshold = 10;
    private const int GroupLineNumbers = 5;
    /// <summary>The agent view lists at most this many functions (the busiest first); the JSON carries all of them.</summary>
    private const int GroupsShown = 10;
    public const string CallersNote = "text-backed: call sites by name, not a resolved call graph";

    public sealed record Hit(string FileId, string Path, int Line, string Text, string Kind);

    /// <summary>
    /// Keeps a content hit only if some whole-word match of <paramref name="name"/> on the line is real
    /// code. Documented heuristic, deliberately small: a match after a `#` is a Python comment; a line that
    /// starts with a triple quote or any quote character is treated as docstring/string text. Multi-line
    /// docstring bodies and `#` inside string literals are not detected. Returns null when dropped.
    /// </summary>
    public static Hit? Classify(ContentSearchResult result, string name)
    {
        var text = result.Preview;
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('"') || trimmed.StartsWith('\'')) return null;
        var word = new Regex($@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(name)}(?![\p{{L}}\p{{N}}_])", RegexOptions.CultureInvariant);
        foreach (Match match in word.Matches(text))
        {
            if (text.AsSpan(0, match.Index).Contains('#')) continue;
            var after = text.AsSpan(match.Index + match.Length).TrimStart();
            var kind = trimmed.StartsWith("from ", StringComparison.Ordinal) || trimmed.StartsWith("import ", StringComparison.Ordinal)
                ? "import"
                : after.StartsWith("(") ? "call" : "usage";
            return new Hit(result.FileId, result.Path, result.Line, trimmed.Length > 120 ? trimmed[..120] : trimmed, kind);
        }
        return null;
    }

    private static int KindRank(string kind) => kind switch { "call" => 0, "usage" => 1, _ => 2 };

    /// <summary>Calls first, then usages, then imports; hits in the target's own file first within a kind.</summary>
    public static List<Hit> Rank(IEnumerable<Hit> hits, string targetPath)
        => hits.OrderBy(hit => KindRank(hit.Kind))
            .ThenBy(hit => string.Equals(hit.Path, targetPath, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(hit => hit.Path, StringComparer.Ordinal)
            .ThenBy(hit => hit.Line)
            .ToList();

    /// <summary>Agent rendering: one line per hit, then a footer; a coverage notice only when output was cut or capped.</summary>
    public static string? Format(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !Prop(data, "mode", out var mode) || mode.GetString() != "text") return null;
        var lines = new List<string>();
        var grouped = Prop(data, "grouped", out var groupedFlag) && groupedFlag.ValueKind == JsonValueKind.True
            && Prop(data, "groups", out var groups) && groups.ValueKind == JsonValueKind.Array;
        if (grouped)
        {
            Prop(data, "groups", out var groupItems);
            foreach (var group in groupItems.EnumerateArray().Take(GroupsShown))
            {
                var count = int.TryParse(Str(group, "count"), out var n) ? n : 0;
                var numbers = Prop(group, "lines", out var lineArray) && lineArray.ValueKind == JsonValueKind.Array ? lineArray.EnumerateArray().Select(line => line.ToString()).ToList() : [];
                var shown = string.Join(",", numbers.Take(GroupLineNumbers)) + (numbers.Count > GroupLineNumbers ? ",…" : "");
                lines.Add($"{Str(group, "enclosing")}  {count} {(count == 1 ? "call" : "calls")}  {Str(group, "path")}:{shown}");
            }
        }
        else if (Prop(data, "references", out var references) && references.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in references.EnumerateArray())
                lines.Add($"{Str(item, "path")}:{Str(item, "line")} {Str(item, "enclosing")}  {Str(item, "text")}");
        }
        var isCallers = Prop(data, "metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object && Str(metadata, "note").Length > 0;
        var total = Str(data, "total");
        var groupCount = grouped && Prop(data, "groups", out var groupList) ? groupList.GetArrayLength() : 0;
        var footer = grouped
            ? $"{total} call sites in {groupCount} {(groupCount == 1 ? "function" : "functions")}{(groupCount > GroupsShown ? $" (top {GroupsShown} shown)" : "")}; target {Str(data, "targetPath")}:{Str(data, "targetLine")}; {CallersNote}; pass --full for every site"
            : isCallers
            ? $"{total} call sites ({Str(data, "calls")} calls); target {Str(data, "targetPath")}:{Str(data, "targetLine")}; {CallersNote}"
            : $"{total} references (calls {Str(data, "calls")}, usages {Str(data, "usages")}, imports {Str(data, "imports")}); target {Str(data, "targetPath")}:{Str(data, "targetLine")}";
        if (Prop(data, "truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True)
            footer += $"; showing {Str(data, "returned")}";
        lines.Add(footer);
        if (Prop(data, "coverage", out var coverage) && coverage.ValueKind == JsonValueKind.Object)
        {
            var cut = Prop(coverage, "outputTruncated", out var outputTruncated) && outputTruncated.ValueKind == JsonValueKind.True;
            var capped = Prop(coverage, "scanCapped", out var scanCapped) && scanCapped.ValueKind == JsonValueKind.True;
            var incomplete = Prop(coverage, "complete", out var complete) && complete.ValueKind == JsonValueKind.False;
            if (cut || capped || incomplete) lines.Add("Search coverage incomplete; more usages may exist.");
        }
        return string.Join("\n", lines);
    }

    private static bool Prop(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default;
        return false;
    }

    private static string Str(JsonElement element, string name) => Prop(element, name, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : string.Empty;
}
