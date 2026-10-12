namespace DevX.Protocol;

/// <summary>
/// Lexical map of one Python source text: which character offsets lie inside a string literal or a comment.
/// A small scanner, not a parser: prefixes (r/b/u/f and pairs), escapes, backslash continuation, single and
/// triple quotes, `#` outside strings. f-string replacement fields count as code (nesting approximated by
/// brace depth); everything else in an f-string is string text.
/// </summary>
public sealed class PythonLexicalRegions
{
    private readonly string _text;
    private readonly List<int> _lineStarts = [0];
    private readonly List<(int Start, int End)> _regions = [];

    private PythonLexicalRegions(string text)
    {
        _text = text;
        for (var i = 0; i < text.Length; i++) if (text[i] == '\n') _lineStarts.Add(i + 1);
    }

    public static PythonLexicalRegions Scan(string text)
    {
        var map = new PythonLexicalRegions(text);
        map.Run();
        return map;
    }

    /// <summary>The raw text of a 1-based line without its line break, or null when out of range.</summary>
    public string? Line(int line)
    {
        if (line < 1 || line > _lineStarts.Count) return null;
        var start = _lineStarts[line - 1];
        var end = line < _lineStarts.Count ? _lineStarts[line] - 1 : _text.Length;
        return _text.Substring(start, end - start).TrimEnd('\r');
    }

    /// <summary>True when the 0-based column of the 1-based line is inside a string literal or comment.</summary>
    public bool IsInside(int line, int column)
    {
        if (line < 1 || line > _lineStarts.Count) return false;
        var offset = _lineStarts[line - 1] + column;
        var low = 0;
        var high = _regions.Count - 1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            var (start, end) = _regions[mid];
            if (offset < start) high = mid - 1;
            else if (offset >= end) low = mid + 1;
            else return true;
        }
        return false;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private void Run()
    {
        var i = 0;
        while (i < _text.Length)
        {
            var c = _text[i];
            if (c == '#')
            {
                var end = i;
                while (end < _text.Length && _text[end] != '\n') end++;
                _regions.Add((i, end));
                i = end;
            }
            else if (c == '"' || c == '\'')
            {
                i = ScanString(i, false);
            }
            else if (char.IsLetter(c) || c == '_')
            {
                var end = i;
                while (end < _text.Length && IsIdentifierChar(_text[end])) end++;
                var word = _text.AsSpan(i, end - i);
                var isPrefix = word.Length <= 2 && end < _text.Length && (_text[end] == '"' || _text[end] == '\'')
                    && word.ToString().All(ch => "rRbBuUfF".Contains(ch));
                if (isPrefix) i = ScanString(end, word.ToString().Any(ch => ch is 'f' or 'F'), i);
                else i = end;
            }
            else i++;
        }
        _regions.Sort();
    }

    /// <summary>Scans the string whose opening quote is at <paramref name="quote"/>; returns the offset after it.</summary>
    private int ScanString(int quote, bool isFormat, int? regionStart = null)
    {
        var q = _text[quote];
        var triple = quote + 2 < _text.Length && _text[quote + 1] == q && _text[quote + 2] == q;
        var segmentStart = regionStart ?? quote;
        var k = quote + (triple ? 3 : 1);
        while (k < _text.Length)
        {
            var c = _text[k];
            if (c == '\\') { k += 2; continue; }
            if (triple)
            {
                if (c == q && k + 2 < _text.Length && _text[k + 1] == q && _text[k + 2] == q) { k += 3; break; }
            }
            else
            {
                if (c == q) { k++; break; }
                if (c == '\n') break;
            }
            if (isFormat && c == '{')
            {
                if (k + 1 < _text.Length && _text[k + 1] == '{') { k += 2; continue; }
                _regions.Add((segmentStart, k + 1));
                k = ScanReplacementField(k + 1);
                segmentStart = k;
                continue;
            }
            k++;
        }
        if (k > _text.Length) k = _text.Length;
        if (k > segmentStart) _regions.Add((segmentStart, k));
        return k;
    }

    /// <summary>Skips an f-string replacement field as code (nested strings become regions); returns the offset of its closing brace.</summary>
    private int ScanReplacementField(int start)
    {
        var depth = 1;
        var k = start;
        while (k < _text.Length)
        {
            var c = _text[k];
            if (c == '{') depth++;
            else if (c == '}') { if (--depth == 0) return k; }
            else if (c == '"' || c == '\'') { k = ScanString(k, false); continue; }
            k++;
        }
        return k;
    }
}
