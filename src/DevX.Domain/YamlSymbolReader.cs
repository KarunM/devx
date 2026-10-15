using System.Text.RegularExpressions;

namespace DevX.Domain;

/// <summary>
/// Lightweight YAML reader (.yml, .yaml). Every top-level key becomes a type spanning its block;
/// the key's direct children are methods. A top-level `jobs:` (GitHub Actions) additionally
/// exposes each job's steps as methods named "job.step" (step name, else id, else uses, else
/// run, else "step N"). Line based, no YAML dependency; anchors, flow style and block scalars are
/// not interpreted, and malformed input yields no symbols instead of an exception.
/// </summary>
public static class YamlSymbolReader
{
    private static readonly string[] Extensions = [".yml", ".yaml"];
    private static readonly Regex KeyLine = new("^(?<indent> *)(?<key>\"[^\"]+\"|'[^']+'|[^\\s#:'\"\\-\\[{][^:#]*?|-?[A-Za-z0-9_.][^:#]*?)\\s*:(?:\\s|$)", RegexOptions.Compiled);

    public static bool CanParse(string filePath)
        => Extensions.Contains(Path.GetExtension(filePath), StringComparer.OrdinalIgnoreCase);

    public static ParsedFile Parse(string filePath)
    {
        try { return Parse(filePath, System.IO.File.ReadAllText(filePath)); }
        catch (IOException) { return new ParsedFile(filePath, null, []); }
    }

    public static ParsedFile Parse(string filePath, string sourceText)
    {
        var @namespace = ConfigSymbolSupport.NamespaceFromPath(filePath, "yaml");
        try
        {
            return new ParsedFile(filePath, @namespace, Scan(sourceText, @namespace));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            return new ParsedFile(filePath, @namespace, []);
        }
    }

    private readonly record struct Key(string Name, int Indent, int Line);

    private static List<ParsedType> Scan(string sourceText, string @namespace)
    {
        var lines = ConfigSymbolSupport.SplitLines(sourceText);
        var types = new List<ParsedType>();
        var keys = KeysAt(lines, 0, lines.Length, 0, exactIndent: true);
        for (var k = 0; k < keys.Count; k++)
        {
            var end = BlockEnd(lines, keys[k].Line, k + 1 < keys.Count ? keys[k + 1].Line : lines.Length + 1);
            var methods = new List<ParsedMethod>();
            var children = KeysAt(lines, keys[k].Line, end, keys[k].Indent + 1, exactIndent: false);
            for (var c = 0; c < children.Count; c++)
            {
                var childEnd = BlockEnd(lines, children[c].Line, c + 1 < children.Count ? children[c + 1].Line : end + 1);
                methods.Add(new ParsedMethod(children[c].Name, keys[k].Name == "jobs" ? "job" : "key", keys[k].Name == "jobs" ? "Job" : "Key", "public", children[c].Line, childEnd, false));
                if (keys[k].Name == "jobs")
                {
                    methods.AddRange(Steps(lines, children[c], childEnd));
                }
            }

            types.Add(new ParsedType(keys[k].Name, "Key", @namespace, "public", keys[k].Line, end, methods, [], []));
        }

        return types;
    }

    /// <summary>Keys whose indent equals the shallowest indent found in (from, to] at or beyond minIndent.</summary>
    private static List<Key> KeysAt(string[] lines, int fromLine, int toLine, int minIndent, bool exactIndent)
    {
        var result = new List<Key>();
        var level = exactIndent ? 0 : -1;
        var inBlockScalar = false;
        var blockIndent = 0;
        if (fromLine >= 1 && Regex.IsMatch(lines[fromLine - 1], ":\\s*[|>][+-]?\\d*\\s*(#.*)?$"))
        {
            inBlockScalar = true;
            blockIndent = lines[fromLine - 1].Length - lines[fromLine - 1].TrimStart(' ').Length;
        }

        for (var line = fromLine + 1; line <= toLine && line <= lines.Length; line++)
        {
            var text = lines[line - 1];
            if (text.Trim().Length == 0 || text.TrimStart().StartsWith('#')) continue;
            var indent = text.Length - text.TrimStart(' ').Length;
            if (inBlockScalar)
            {
                if (indent > blockIndent) continue;
                inBlockScalar = false;
            }

            if (text.StartsWith("---") || text.StartsWith("...")) continue;
            var m = KeyLine.Match(text);
            if (!m.Success || text.TrimStart().StartsWith("- ")) continue;
            if (Regex.IsMatch(text, ":\\s*[|>][+-]?\\d*\\s*(#.*)?$"))
            {
                inBlockScalar = true;
                blockIndent = indent;
            }

            if (indent < minIndent) continue;
            if (level < 0) level = indent;
            if (indent == level) result.Add(new Key(m.Groups["key"].Value.Trim().Trim('"', '\''), indent, line));
        }

        return result;
    }

    /// <summary>Last line of the block started by the key at <paramref name="startLine"/>: up to just before <paramref name="nextLine"/>, minus trailing blanks/comments.</summary>
    private static int BlockEnd(string[] lines, int startLine, int nextLine)
    {
        var end = Math.Min(nextLine - 1, lines.Length);
        while (end > startLine && (lines[end - 1].Trim().Length == 0 || lines[end - 1].TrimStart().StartsWith('#'))) end--;
        return Math.Max(end, startLine);
    }

    private static IEnumerable<ParsedMethod> Steps(string[] lines, Key job, int jobEnd)
    {
        // Find the `steps:` key among the job's children.
        var jobKeys = KeysAt(lines, job.Line, jobEnd, job.Indent + 1, exactIndent: false);
        var steps = jobKeys.FirstOrDefault(key => key.Name == "steps");
        if (steps.Line == 0) yield break;
        var stepsEnd = BlockEnd(lines, steps.Line, jobKeys.SkipWhile(key => key.Line <= steps.Line).Select(key => key.Line).DefaultIfEmpty(jobEnd + 1).First());
        var starts = new List<(int Line, int Indent)>();
        var itemIndent = -1;
        for (var line = steps.Line + 1; line <= stepsEnd; line++)
        {
            var text = lines[line - 1];
            var trimmed = text.TrimStart(' ');
            if (trimmed.StartsWith("- ") || trimmed == "-")
            {
                var indent = text.Length - trimmed.Length;
                if (itemIndent < 0) itemIndent = indent;
                if (indent == itemIndent) starts.Add((line, indent));
            }
        }

        for (var s = 0; s < starts.Count; s++)
        {
            var end = BlockEnd(lines, starts[s].Line, s + 1 < starts.Count ? starts[s + 1].Line : stepsEnd + 1);
            var name = StepName(lines, starts[s].Line, end, starts[s].Indent) ?? $"step {s + 1}";
            yield return new ParsedMethod($"{job.Name}.{name}", "step", "Step", "public", starts[s].Line, end, false);
        }
    }

    private static string? StepName(string[] lines, int start, int end, int itemIndent)
    {
        string? id = null, uses = null, run = null;
        for (var line = start; line <= end; line++)
        {
            var text = lines[line - 1];
            var body = line == start ? text.TrimStart(' ')[1..].TrimStart() : text.TrimStart(' ');
            var indent = line == start ? itemIndent + 2 : text.Length - body.Length;
            if (indent != itemIndent + 2 && line != start) continue;
            var m = Regex.Match(body, "^(?<k>name|id|uses|run)\\s*:\\s*(?<v>.*?)\\s*$");
            if (!m.Success) continue;
            var value = m.Groups["v"].Value.Trim('"', '\'');
            if (value.Length == 0 || value[0] is '|' or '>') value = line < end ? lines[line].Trim() : string.Empty;
            if (value.Length == 0) continue;
            switch (m.Groups["k"].Value)
            {
                case "name": return value;
                case "id": id ??= value; break;
                case "uses": uses ??= value; break;
                default: run ??= value.Length > 60 ? value[..60] : value; break;
            }
        }

        return id ?? uses ?? run;
    }
}
