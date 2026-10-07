using System.Text;
using System.Text.RegularExpressions;

namespace DevX.Domain;

/// <summary>
/// Parses JavaScript into the ParsedFile shape the C#, Python and Markdown readers produce, so
/// `search symbol`, `snapshot` and `read method` work on .js files.
///
/// Mapping:
///   * a synthetic Module type named after the file holds top-level functions
///     (`function f(`, `const f = (...) =>`, `const f = function`) as methods and top-level
///     `const|let|var NAME = [` / `{` declarations as fields;
///   * every `class Name` becomes its own type with its methods as ParsedMethods.
/// Every method carries a body span so `read method` returns source.
///
/// No parser dependency: strings, template literals, comments and regex literals are masked
/// out first, then declarations are found at brace depth 0 (class members at depth 1) and spans
/// are closed by bracket matching.
/// </summary>
public static class JavaScriptSymbolReader
{
    private static readonly string[] Extensions = [".js", ".mjs", ".cjs", ".jsx"];

    private static readonly Regex FunctionDecl = new(
        @"^(?:export\s+)?(?:default\s+)?(?:async\s+)?function\s*\*?\s*(?<name>[\w$]+)\s*\(", RegexOptions.Compiled);
    private static readonly Regex VarDecl = new(
        @"^(?:export\s+)?(?<kw>const|let|var)\s+(?<name>[\w$]+)\s*=\s*", RegexOptions.Compiled);
    private static readonly Regex ClassDecl = new(
        @"^(?:export\s+)?(?:default\s+)?class\s+(?<name>[\w$]+)", RegexOptions.Compiled);
    private static readonly Regex MemberDecl = new(
        @"^(?:static\s+)?(?:async\s+)?(?:(?:get|set)\s+)?\*?\s*(?<name>#?[\w$]+)\s*\(", RegexOptions.Compiled);
    // `(function (..) {`, `;(function`, `(function(){`, `(() => {`, `(async () => {`, `!function(){`
    private static readonly Regex IifeOpen = new(
        @"^;?\s*(?:[!(]\s*)+(?:async\s+)?(?:function\b[^{]*|\([^)]*\)\s*=>\s*|[\w$]+\s*=>\s*)\{", RegexOptions.Compiled);
    private static readonly HashSet<string> NotMembers =
        ["if", "for", "while", "switch", "catch", "return", "function", "with", "else", "do", "new", "typeof"];

    public static bool CanParse(string filePath)
        => Extensions.Contains(Path.GetExtension(filePath), StringComparer.OrdinalIgnoreCase);

    public static ParsedFile Parse(string filePath)
    {
        string sourceText;
        try
        {
            sourceText = System.IO.File.ReadAllText(filePath);
        }
        catch (IOException)
        {
            return new ParsedFile(filePath, null, []);
        }

        return Parse(filePath, sourceText);
    }

    public static ParsedFile Parse(string filePath, string sourceText)
    {
        var text = (sourceText ?? string.Empty).Replace("\r\n", "\n");
        var masked = Mask(text);
        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < masked.Length; i++)
        {
            if (masked[i] == '\n')
            {
                lineStarts.Add(i + 1);
            }
        }

        var lineCount = lineStarts.Count;
        var @namespace = NamespaceFromPath(filePath);
        var moduleName = Path.GetFileNameWithoutExtension(filePath);
        if (string.IsNullOrEmpty(moduleName))
        {
            moduleName = "module";
        }

        var moduleMethods = new List<ParsedMethod>();
        var moduleFields = new List<ParsedField>();
        var classes = new List<ParsedType>();

        var depth = 0;
        var iifeOpen = -1;
        var iifeClose = -1;
        var iifeBodyDepth = 0;
        for (var line = 0; line < lineCount; line++)
        {
            var start = lineStarts[line];
            var end = line + 1 < lineCount ? lineStarts[line + 1] - 1 : masked.Length;
            var lineText = masked[start..end];
            var lineDepth = depth;
            foreach (var ch in lineText)
            {
                if (ch is '{' or '(' or '[') depth++;
                else if (ch is '}' or ')' or ']') depth--;
            }

            // The outermost IIFE body counts as module level: its declarations are treated as if unwrapped.
            if (iifeClose >= 0 && start > iifeClose)
            {
                iifeOpen = iifeClose = -1;
            }

            var inIife = iifeClose >= 0 && start > iifeOpen;
            if (lineDepth == 0 && iifeClose < 0)
            {
                var im = IifeOpen.Match(lineText);
                if (im.Success)
                {
                    iifeOpen = start + im.Length - 1;
                    iifeClose = MatchClose(masked, iifeOpen);
                    iifeBodyDepth = depth;
                    continue;
                }
            }

            if (inIife)
            {
                lineDepth -= iifeBodyDepth;
                var lead = lineText.Length - lineText.TrimStart().Length;
                start += lead;
                lineText = lineText[lead..];
            }

            if (lineDepth != 0 || lineText.Length == 0 || char.IsWhiteSpace(lineText[0]))
            {
                continue;
            }

            var m = FunctionDecl.Match(lineText);
            if (m.Success)
            {
                var stop = BodyEnd(masked, start + m.Length - 1);
                moduleMethods.Add(Method(m.Groups["name"].Value, lineText, line + 1, LineOf(lineStarts, stop)));
                continue;
            }

            m = ClassDecl.Match(lineText);
            if (m.Success)
            {
                var bodyStart = masked.IndexOf('{', start + m.Length);
                if (bodyStart < 0)
                {
                    continue;
                }

                var stop = MatchClose(masked, bodyStart);
                var endLine = LineOf(lineStarts, stop);
                classes.Add(new ParsedType(
                    m.Groups["name"].Value, "Class", @namespace, "public", line + 1, endLine,
                    ClassMethods(masked, lineStarts, bodyStart, stop), [], []));
                continue;
            }

            m = VarDecl.Match(lineText);
            if (m.Success)
            {
                var valueStart = start + m.Length;
                var stop = StatementEnd(masked, valueStart);
                var endLine = LineOf(lineStarts, stop);
                var name = m.Groups["name"].Value;
                if (IsFunctionValue(masked, valueStart))
                {
                    moduleMethods.Add(Method(name, lineText, line + 1, endLine));
                }
                else if (valueStart < masked.Length && masked[valueStart] is '[' or '{')
                {
                    moduleFields.Add(new ParsedField(name, m.Groups["kw"].Value, "public", line + 1, endLine));
                }
            }
        }

        var types = new List<ParsedType>();
        if (moduleMethods.Count > 0 || moduleFields.Count > 0)
        {
            types.Add(new ParsedType(moduleName, "Module", @namespace, "public", 1, Math.Max(lineCount, 1),
                moduleMethods, [], moduleFields));
        }

        types.AddRange(classes);
        return new ParsedFile(filePath, @namespace, types);
    }

    private static ParsedMethod Method(string name, string lineText, int startLine, int endLine)
        => new(name, lineText.TrimEnd().TrimEnd('{').TrimEnd(), "any", "public", startLine, endLine, name == "constructor");

    private static List<ParsedMethod> ClassMethods(string masked, List<int> lineStarts, int bodyStart, int bodyEnd)
    {
        var methods = new List<ParsedMethod>();
        var depth = 0;
        var firstLine = LineOf(lineStarts, bodyStart);
        var lastLine = LineOf(lineStarts, bodyEnd);
        for (var line = firstLine; line <= lastLine; line++)
        {
            var start = lineStarts[line - 1];
            var end = line < lineStarts.Count ? lineStarts[line] - 1 : masked.Length;
            var from = line == firstLine ? bodyStart : start;
            var to = Math.Min(end, bodyEnd + 1);
            var segment = masked[from..to];
            var lineDepth = line == firstLine ? 0 : depth;
            if (line == firstLine)
            {
                depth = 0;
            }

            foreach (var ch in segment)
            {
                if (ch is '{' or '(' or '[') depth++;
                else if (ch is '}' or ')' or ']') depth--;
            }

            if (line == firstLine || lineDepth != 1)
            {
                continue;
            }

            var trimmed = masked[start..end].TrimStart();
            var m = MemberDecl.Match(trimmed);
            if (!m.Success || NotMembers.Contains(m.Groups["name"].Value))
            {
                continue;
            }

            var offset = start + (masked[start..end].Length - trimmed.Length);
            var stop = BodyEnd(masked, offset + m.Length - 1);
            if (stop > bodyEnd)
            {
                continue;
            }

            methods.Add(Method(m.Groups["name"].Value, trimmed, line, LineOf(lineStarts, stop)));
        }

        return methods;
    }

    /// <summary>True when the initializer is `function`, `async function`, or an arrow function.</summary>
    private static bool IsFunctionValue(string masked, int pos)
    {
        var rest = masked.AsSpan(pos);
        if (rest.StartsWith("function") || rest.StartsWith("async function"))
        {
            return true;
        }

        if (rest.StartsWith("async"))
        {
            pos += 5;
            while (pos < masked.Length && char.IsWhiteSpace(masked[pos])) pos++;
        }

        if (pos >= masked.Length)
        {
            return false;
        }

        if (masked[pos] == '(')
        {
            var close = MatchClose(masked, pos);
            if (close >= masked.Length)
            {
                return false;
            }

            pos = close + 1;
        }
        else
        {
            while (pos < masked.Length && (char.IsLetterOrDigit(masked[pos]) || masked[pos] is '_' or '$')) pos++;
        }

        while (pos < masked.Length && char.IsWhiteSpace(masked[pos])) pos++;
        return pos + 1 < masked.Length && masked[pos] == '=' && masked[pos + 1] == '>';
    }

    /// <summary>Index of the bracket closing the one at <paramref name="open"/> (or text length).</summary>
    private static int MatchClose(string masked, int open)
    {
        var depth = 0;
        for (var i = open; i < masked.Length; i++)
        {
            var ch = masked[i];
            if (ch is '{' or '(' or '[') depth++;
            else if (ch is '}' or ')' or ']')
            {
                depth--;
                if (depth == 0) return i;
            }
        }

        return masked.Length - 1;
    }

    /// <summary>End of a function-like declaration: the `}` closing the first body brace after the parameter list.</summary>
    private static int BodyEnd(string masked, int parenOpen)
    {
        var close = MatchClose(masked, parenOpen);
        var brace = masked.IndexOf('{', Math.Min(close + 1, masked.Length - 1));
        return brace < 0 ? close : MatchClose(masked, brace);
    }

    /// <summary>End of a variable statement: `;` at depth 0, or a depth-0 newline not continued by an operator.</summary>
    private static int StatementEnd(string masked, int pos)
    {
        var depth = 0;
        var arrowBodySeen = false;
        for (var i = pos; i < masked.Length; i++)
        {
            var ch = masked[i];
            if (ch is '{' or '(' or '[') depth++;
            else if (ch is '}' or ')' or ']') depth--;
            else if (depth == 0 && ch == ';') return i;
            else if (depth == 0 && ch == '\n')
            {
                var j = i - 1;
                while (j >= pos && char.IsWhiteSpace(masked[j])) j--;
                if (j < pos || "=>+-*/&|?:,(".IndexOf(masked[j]) < 0)
                {
                    var k = i + 1;
                    while (k < masked.Length && char.IsWhiteSpace(masked[k])) k++;
                    // A leading `.`/operator on the next line continues the expression.
                    if (k < masked.Length && ".?:+-*/&|,".IndexOf(masked[k]) >= 0 && !(masked[k] == '/'))
                    {
                        continue;
                    }

                    return i - 1;
                }
            }

            arrowBodySeen |= ch == '>';
        }

        return masked.Length - 1;
    }

    private static int LineOf(List<int> lineStarts, int offset)
    {
        var idx = lineStarts.BinarySearch(Math.Max(offset, 0));
        return idx >= 0 ? idx + 1 : ~idx;
    }

    /// <summary>
    /// Replaces comment, string, template and regex-literal contents with spaces (newlines kept) so
    /// bracket counting and declaration matching cannot be fooled by them.
    /// </summary>
    internal static string Mask(string text)
    {
        var sb = new StringBuilder(text.Length);
        var prevSig = '\0';
        var prevWord = string.Empty;
        var word = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                while (i < text.Length && text[i] != '\n') { sb.Append(' '); i++; }
                continue;
            }

            if (c == '/' && next == '*')
            {
                sb.Append("  ");
                i += 2;
                while (i < text.Length && !(text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/'))
                {
                    sb.Append(text[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                if (i < text.Length) { sb.Append("  "); i += 2; }
                continue;
            }

            if (c is '"' or '\'' or '`')
            {
                sb.Append(c);
                i++;
                while (i < text.Length && text[i] != c)
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        sb.Append(' ');
                        i++;
                    }
                    else if (text[i] == '\n' && c != '`')
                    {
                        break;
                    }

                    sb.Append(text[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                if (i < text.Length && text[i] == c) { sb.Append(c); i++; }
                prevSig = c;
                word.Clear();
                continue;
            }

            if (c == '/' && (prevSig == '\0' || "(,=:[!&|?{};".IndexOf(prevSig) >= 0 || prevWord is "return" or "typeof"))
            {
                var j = i + 1;
                var inClass = false;
                var closed = false;
                while (j < text.Length && text[j] != '\n')
                {
                    if (text[j] == '\\') j++;
                    else if (text[j] == '[') inClass = true;
                    else if (text[j] == ']') inClass = false;
                    else if (text[j] == '/' && !inClass) { closed = true; break; }
                    j++;
                }

                if (closed)
                {
                    sb.Append('/');
                    sb.Append(' ', j - i - 1);
                    sb.Append('/');
                    i = j + 1;
                    prevSig = '/';
                    word.Clear();
                    continue;
                }
            }

            sb.Append(c);
            if (char.IsLetterOrDigit(c) || c is '_' or '$')
            {
                word.Append(c);
            }
            else
            {
                if (word.Length > 0) { prevWord = word.ToString(); word.Clear(); }
                if (!char.IsWhiteSpace(c))
                {
                    prevSig = c;
                    prevWord = string.Empty;
                }
            }

            if (char.IsLetterOrDigit(c) || c is '_' or '$')
            {
                prevSig = c;
            }

            i++;
        }

        return sb.ToString();
    }

    private static string NamespaceFromPath(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        var withoutExtension = extension.Length > 0 && CanParse(filePath) ? filePath[..^extension.Length] : filePath;
        var segments = withoutExtension.Replace('\\', '/').Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return "javascript";
        }

        var take = Math.Min(segments.Length, 3);
        return string.Join('.', segments[^take..]);
    }
}
