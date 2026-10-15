using System.Text;
using System.Text.RegularExpressions;

namespace DevX.Domain;

/// <summary>
/// Lightweight Terraform / HCL reader (.tf, .tfvars, .tfbackend, .hcl incl. .tftest.hcl). Each
/// top-level block becomes a type spanning the whole block so `read method` returns it:
/// resource "t" "n" -> t.n, data "t" "n" -> data.t.n, module/variable/output/provider/run/check
/// -> kind.name, locals/terraform -> their own name. Top-level `name = value` attributes (tfvars)
/// become fields of one document type. No dependency; comments, strings and heredocs are masked
/// before braces are counted, and a malformed file yields whatever blocks closed (never throws).
/// </summary>
public static class HclSymbolReader
{
    private static readonly string[] Extensions = [".tf", ".tfvars", ".tfbackend", ".hcl"];

    private static readonly Regex BlockHeader = new(
        "^(?<kind>[A-Za-z_][\\w-]*)(?<labels>(?:\\s+(?:\"[^\"]*\"|[\\w.-]+))*)\\s*\\{", RegexOptions.Compiled);
    private static readonly Regex Label = new("\"([^\"]*)\"|([\\w.-]+)", RegexOptions.Compiled);
    private static readonly Regex Attribute = new("^(?<name>[A-Za-z_][\\w-]*)\\s*=(?!=)", RegexOptions.Compiled);
    private static readonly Regex LocalKey = new("^\\s*(?<name>[A-Za-z_][\\w-]*)\\s*=(?!=)", RegexOptions.Compiled);

    public static bool CanParse(string filePath)
        => Extensions.Contains(Path.GetExtension(filePath), StringComparer.OrdinalIgnoreCase);

    public static ParsedFile Parse(string filePath)
    {
        try { return Parse(filePath, System.IO.File.ReadAllText(filePath)); }
        catch (IOException) { return new ParsedFile(filePath, null, []); }
    }

    public static ParsedFile Parse(string filePath, string sourceText)
    {
        var @namespace = ConfigSymbolSupport.NamespaceFromPath(filePath, "terraform");
        try
        {
            return new ParsedFile(filePath, @namespace, Scan(filePath, sourceText, @namespace));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            return new ParsedFile(filePath, @namespace, []);
        }
    }

    private static List<ParsedType> Scan(string filePath, string sourceText, string @namespace)
    {
        var lines = ConfigSymbolSupport.SplitLines(sourceText);
        var masked = Mask(lines);
        var types = new List<ParsedType>();
        var attributes = new List<ParsedField>();
        var depth = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = masked[i];
            if (depth == 0 && line.Trim().Length > 0)
            {
                var text = line.TrimStart();
                var header = BlockHeader.Match(text);
                if (header.Success)
                {
                    var end = BlockEnd(masked, i);
                    if (end >= 0)
                    {
                        var kind = header.Groups["kind"].Value;
                        var original = BlockHeader.Match(lines[i].TrimStart());
                        var labels = Label.Matches(original.Success ? original.Groups["labels"].Value : string.Empty)
                            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToArray();
                        var fields = kind == "locals" ? LocalFields(masked, i, end) : [];
                        types.Add(new ParsedType(NameOf(kind, labels), KindOf(kind), @namespace, "public", i + 1, end + 1, [], [], fields));
                        i = end;
                        depth = 0;
                        continue;
                    }

                    // Unterminated block: record nothing for it and stop (malformed file).
                    break;
                }

                var attribute = Attribute.Match(text);
                if (attribute.Success)
                {
                    var end = AttributeEnd(masked, i);
                    attributes.Add(new ParsedField(attribute.Groups["name"].Value, "attribute", "public", i + 1, end + 1));
                    i = end;
                    continue;
                }
            }

            foreach (var ch in line)
            {
                if (ch == '{') depth++;
                else if (ch == '}') depth = Math.Max(0, depth - 1);
            }
        }

        if (attributes.Count > 0)
        {
            var name = Path.GetFileNameWithoutExtension(filePath);
            types.Insert(0, new ParsedType(name.Length == 0 ? "variables" : name, "Variables", @namespace, "public", 1, Math.Max(lines.Length, 1), [], [], attributes));
        }

        return types;
    }

    private static string KindOf(string kind) => kind switch
    {
        "resource" => "Resource",
        "data" => "Data",
        "module" => "Module",
        "variable" => "Variable",
        "output" => "Output",
        "locals" => "Locals",
        "provider" => "Provider",
        "terraform" => "Terraform",
        "run" => "Run",
        _ => char.ToUpperInvariant(kind[0]) + kind[1..],
    };

    private static string NameOf(string kind, string[] labels) => kind switch
    {
        "resource" when labels.Length >= 2 => $"{labels[0]}.{labels[1]}",
        "data" when labels.Length >= 2 => $"data.{labels[0]}.{labels[1]}",
        "locals" or "terraform" => kind,
        _ when labels.Length > 0 => $"{kind}.{string.Join('.', labels)}",
        _ => kind,
    };

    private static ParsedField[] LocalFields(string[] masked, int start, int end)
    {
        var fields = new List<ParsedField>();
        var depth = 0;
        for (var i = start; i <= end; i++)
        {
            if (depth == 1)
            {
                var m = LocalKey.Match(masked[i]);
                if (m.Success)
                {
                    fields.Add(new ParsedField(m.Groups["name"].Value, "local", "public", i + 1, AttributeEnd(masked, i, end) + 1));
                }
            }

            foreach (var ch in masked[i])
            {
                if (ch is '{' or '(' or '[') depth++;
                else if (ch is '}' or ')' or ']') depth--;
            }
        }

        return [.. fields];
    }

    /// <summary>0-based line of the `}` closing the block opened on line <paramref name="start"/>, or -1.</summary>
    private static int BlockEnd(string[] masked, int start)
    {
        var depth = 0;
        for (var i = start; i < masked.Length; i++)
        {
            foreach (var ch in masked[i])
            {
                if (ch == '{') depth++;
                else if (ch == '}')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
        }

        return -1;
    }

    /// <summary>Last line of an attribute: continues while ( [ { are open.</summary>
    private static int AttributeEnd(string[] masked, int start, int limit = int.MaxValue)
    {
        var depth = 0;
        for (var i = start; i < masked.Length && i <= limit; i++)
        {
            foreach (var ch in masked[i])
            {
                if (ch is '{' or '(' or '[') depth++;
                else if (ch is '}' or ')' or ']') depth--;
            }

            if (depth <= 0) return i;
        }

        return Math.Min(masked.Length - 1, limit);
    }

    /// <summary>Blanks comments, string contents and heredoc bodies (keeping line count and quotes).</summary>
    private static string[] Mask(string[] lines)
    {
        var result = new string[lines.Length];
        var inBlockComment = false;
        string? heredoc = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (heredoc is not null)
            {
                result[i] = string.Empty;
                if (line.Trim() == heredoc) heredoc = null;
                continue;
            }

            var sb = new StringBuilder(line.Length);
            var inString = false;
            for (var j = 0; j < line.Length; j++)
            {
                var c = line[j];
                var next = j + 1 < line.Length ? line[j + 1] : '\0';
                if (inBlockComment)
                {
                    if (c == '*' && next == '/') { inBlockComment = false; j++; }
                    continue;
                }

                if (inString)
                {
                    if (c == '\\') { j++; continue; }
                    if (c == '"') { inString = false; sb.Append('"'); }
                    continue;
                }

                if (c == '#' || (c == '/' && next == '/')) break;
                if (c == '/' && next == '*') { inBlockComment = true; j++; continue; }
                if (c == '"') { inString = true; sb.Append('"'); continue; }
                if (c == '<' && next == '<')
                {
                    var tag = Regex.Match(line[j..], "^<<-?\\s*(?<tag>[A-Za-z_][\\w]*)\\s*$");
                    if (tag.Success) { heredoc = tag.Groups["tag"].Value; break; }
                }

                sb.Append(c);
            }

            result[i] = sb.ToString();
        }

        return result;
    }
}
