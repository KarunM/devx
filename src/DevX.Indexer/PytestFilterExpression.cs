using System.Text.RegularExpressions;

namespace DevX.Indexer;

/// <summary>Parses the bounded subset of pytest -k: terms combined with and/or/not and parentheses.</summary>
internal sealed class PytestFilterExpression
{
    private const int MaximumLength = 4096;
    private const int MaximumTokens = 256;
    private const int MaximumNesting = 64;
    private static readonly Regex TokenPattern = new(@"\G\s*(?<token>\(|\)|[A-Za-z0-9_./:-]+)", RegexOptions.CultureInvariant);
    private readonly IReadOnlyList<string> _tokens;
    private int _position;
    private int _nesting;

    private PytestFilterExpression(IReadOnlyList<string> tokens) => _tokens = tokens;

    public static bool TryParse(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression) || expression.Length > MaximumLength) return false;

        var tokens = new List<string>();
        var offset = 0;
        while (offset < expression.Length)
        {
            if (string.IsNullOrWhiteSpace(expression[offset..])) break;
            var match = TokenPattern.Match(expression, offset);
            if (!match.Success || match.Length == 0) return false;
            tokens.Add(match.Groups["token"].Value);
            if (tokens.Count > MaximumTokens) return false;
            offset += match.Length;
        }

        if (tokens.Count == 0) return false;
        var parser = new PytestFilterExpression(tokens);
        return parser.ParseOr() && parser._position == parser._tokens.Count;
    }

    private bool ParseOr()
    {
        if (!ParseAnd()) return false;
        while (Consume("or"))
            if (!ParseAnd()) return false;
        return true;
    }

    private bool ParseAnd()
    {
        if (!ParseNot()) return false;
        while (Consume("and"))
            if (!ParseNot()) return false;
        return true;
    }

    private bool ParseNot()
    {
        var negations = 0;
        while (Consume("not"))
            if (++negations > MaximumNesting) return false;
        return ParsePrimary();
    }

    private bool ParsePrimary()
    {
        if (Consume("("))
        {
            if (++_nesting > MaximumNesting) return false;
            var valid = ParseOr() && Consume(")");
            _nesting--;
            if (!valid) return false;
            return true;
        }

        if (_position >= _tokens.Count) return false;
        var token = _tokens[_position];
        if (token is "(" or ")" or "and" or "or" or "not") return false;
        _position++;
        return true;
    }

    private bool Consume(string token)
    {
        if (_position >= _tokens.Count || _tokens[_position] != token) return false;
        _position++;
        return true;
    }
}
