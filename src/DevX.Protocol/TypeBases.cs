using System.Text.RegularExpressions;

namespace DevX.Protocol;

/// <summary>One declared base of a type in the outline: the name as written, and where it lives when it resolves to one indexed type.</summary>
public sealed record TypeBase(string Name,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? QualifiedName = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Path = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] int? StartLine = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] int? EndLine = null)
{
    [System.Text.Json.Serialization.JsonIgnore] public bool Resolved => Path is not null;
}

/// <summary>Reads the base list out of a type's declaration text (Python `class N(A, m.B, metaclass=M):`, C# `class N : A, IB`).</summary>
public static class TypeBases
{
    private static readonly Regex Identifier = new(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$", RegexOptions.CultureInvariant);
    private static readonly Regex PythonClass = new(@"^\s*class\s+[A-Za-z_][A-Za-z0-9_]*\s*\(", RegexOptions.CultureInvariant);
    private static readonly Regex CSharpDeclaration = new(@"\b(?:class|struct|interface|record)\s+[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> Parse(string filePath, IReadOnlyList<string> declarationLines)
    {
        var text = string.Join("\n", declarationLines);
        if (filePath.EndsWith(".py", StringComparison.OrdinalIgnoreCase)) return ParsePython(text);
        if (filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return ParseCSharp(text);
        return [];
    }

    private static IReadOnlyList<string> ParsePython(string text)
    {
        var match = PythonClass.Match(text);
        if (!match.Success) return [];
        var start = match.Index + match.Length;
        var depth = 1;
        var end = -1;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] is '(' or '[' or '{') depth++;
            else if (text[i] is ')' or ']' or '}') { depth--; if (depth == 0) { end = i; break; } }
        }
        if (end < 0) return [];
        return Clean(SplitTopLevel(text[start..end]), requireNoEquals: true);
    }

    private static IReadOnlyList<string> ParseCSharp(string text)
    {
        var declaration = CSharpDeclaration.Match(text);
        if (!declaration.Success) return [];
        var i = declaration.Index + declaration.Length;
        // Skip generic parameters and a record's primary constructor, then expect ':'.
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i < text.Length && (text[i] == '<' || text[i] == '('))
            {
                var open = text[i];
                var close = open == '<' ? '>' : ')';
                var depth = 0;
                for (; i < text.Length; i++)
                {
                    if (text[i] == open) depth++;
                    else if (text[i] == close && --depth == 0) { i++; break; }
                }
                continue;
            }
            break;
        }
        if (i >= text.Length || text[i] != ':') return [];
        i++;
        var tail = new System.Text.StringBuilder();
        var angle = 0;
        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '<' || c == '(') angle++;
            else if (c == '>' || c == ')') angle--;
            else if (angle == 0 && (c == '{' || c == ';')) break;
            else if (angle == 0 && c == 'w' && Regex.IsMatch(text[i..], @"^where\s", RegexOptions.CultureInvariant) && (i == 0 || char.IsWhiteSpace(text[i - 1]))) break;
            tail.Append(c);
        }
        return Clean(SplitTopLevel(tail.ToString()), requireNoEquals: false);
    }

    private static List<string> SplitTopLevel(string list)
    {
        var parts = new List<string>();
        var depth = 0;
        var current = new System.Text.StringBuilder();
        foreach (var c in list)
        {
            if (c is '(' or '[' or '{' or '<') depth++;
            else if (c is ')' or ']' or '}' or '>') depth--;
            if (c == ',' && depth == 0) { parts.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        parts.Add(current.ToString());
        return parts;
    }

    private static IReadOnlyList<string> Clean(IEnumerable<string> parts, bool requireNoEquals)
    {
        var names = new List<string>();
        foreach (var raw in parts)
        {
            var part = raw.Trim();
            if (part.Length == 0) continue;
            if (requireNoEquals && Regex.IsMatch(part, @"^[A-Za-z_][A-Za-z0-9_]*\s*=(?!=)", RegexOptions.CultureInvariant)) continue;
            if (part.StartsWith('*')) continue;
            if (part.Contains('(')) continue; // a call such as with_metaclass(M, B) names no single base
            var bracket = part.IndexOf('[');
            if (bracket >= 0) part = part[..bracket].Trim();
            var angle = part.IndexOf('<');
            if (angle >= 0) part = part[..angle].Trim();
            part = Regex.Replace(part, @"\s+", "");
            if (part.StartsWith("global::", StringComparison.Ordinal)) part = part["global::".Length..];
            if (part == "object" || !Identifier.IsMatch(part) || names.Contains(part)) continue;
            names.Add(part);
        }
        return names;
    }

    public static string FormatLine(IReadOnlyList<TypeBase> bases)
        => "bases: " + string.Join("  ", bases.Select(item => item.Resolved ? $"{item.Name} {item.Path}:{item.StartLine}-{item.EndLine}" : $"{item.Name} -"));
}
