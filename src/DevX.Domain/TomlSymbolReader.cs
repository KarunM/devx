using System.Text.RegularExpressions;

namespace DevX.Domain;

/// <summary>
/// Lightweight TOML reader (.toml). Each table `[a.b]` / array of tables `[[a.b]]` becomes a type
/// spanning its lines, with its keys as fields; keys above the first table form a "(root)" type.
/// Multi-line strings are skipped so a `[x]` inside one is not a table. Never throws.
/// </summary>
public static class TomlSymbolReader
{
    private static readonly Regex Table = new("^\\s*\\[(?<arr>\\[)?\\s*(?<name>[^\\[\\]#]+?)\\s*\\]\\]?\\s*(#.*)?$", RegexOptions.Compiled);
    private static readonly Regex KeyValue = new("^\\s*(?<key>\"[^\"]+\"|'[^']+'|[A-Za-z0-9_.\\-]+)\\s*=", RegexOptions.Compiled);

    public static bool CanParse(string filePath)
        => Path.GetExtension(filePath).Equals(".toml", StringComparison.OrdinalIgnoreCase);

    public static ParsedFile Parse(string filePath)
    {
        try { return Parse(filePath, System.IO.File.ReadAllText(filePath)); }
        catch (IOException) { return new ParsedFile(filePath, null, []); }
    }

    public static ParsedFile Parse(string filePath, string sourceText)
    {
        var @namespace = ConfigSymbolSupport.NamespaceFromPath(filePath, "toml");
        try
        {
            return new ParsedFile(filePath, @namespace, Scan(sourceText, @namespace));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            return new ParsedFile(filePath, @namespace, []);
        }
    }

    private static List<ParsedType> Scan(string sourceText, string @namespace)
    {
        var lines = ConfigSymbolSupport.SplitLines(sourceText);
        var types = new List<ParsedType>();
        string? name = null;
        var isArray = false;
        var start = 1;
        var fields = new List<ParsedField>();
        string? multiline = null;

        void Close(int endLine)
        {
            var end = ConfigSymbolSupport.TrimTrailingBlank(lines, start, endLine);
            if (name is null)
            {
                if (fields.Count > 0) types.Add(new ParsedType("(root)", "Table", @namespace, "public", 1, end, [], [], [.. fields]));
            }
            else
            {
                types.Add(new ParsedType(name, isArray ? "ArrayTable" : "Table", @namespace, "public", start, end, [], [], [.. fields]));
            }

            fields = [];
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var text = lines[i];
            if (multiline is not null)
            {
                if (text.Contains(multiline)) multiline = null;
                continue;
            }

            var table = Table.Match(text);
            if (table.Success && !text.TrimStart().StartsWith('#'))
            {
                Close(i);
                name = table.Groups["name"].Value.Trim();
                isArray = table.Groups["arr"].Success;
                start = i + 1;
                continue;
            }

            var kv = KeyValue.Match(text);
            if (!kv.Success) continue;
            var rest = text[kv.Length..];
            var opener = rest.Contains("\"\"\"") ? "\"\"\"" : rest.Contains("'''") ? "'''" : null;
            var fieldEnd = i + 1;
            if (opener is not null && rest.IndexOf(opener, rest.IndexOf(opener) + 3) < 0)
            {
                multiline = opener;
                var j = i + 1;
                while (j < lines.Length && !lines[j].Contains(opener)) j++;
                fieldEnd = Math.Min(j + 1, lines.Length);
            }

            fields.Add(new ParsedField(kv.Groups["key"].Value.Trim('"', '\''), "key", "public", i + 1, fieldEnd));
        }

        Close(lines.Length);
        return types;
    }
}
