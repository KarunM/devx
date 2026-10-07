using DevX.Domain;

namespace DevX.Indexer;

/// <summary>
/// Dispatches parsing to the right per-language parser based on file extension. Keeps
/// RepositoryIndexingService language-agnostic instead of hardcoding CSharpRoslynParser.
/// </summary>
public sealed class CompositeLanguageParser : ILanguageParser
{
    private readonly IReadOnlyList<ILanguageParser> _parsers;

    public CompositeLanguageParser(IReadOnlyList<ILanguageParser> parsers)
    {
        _parsers = parsers;
    }

    public CompositeLanguageParser()
        : this([new CSharpRoslynParser(), new PythonAstParser(), new MarkdownParser(), new JavaScriptParser(), new JsonParser(), new HtmlParser()])
    {
    }

    public bool CanParse(string filePath) => _parsers.Any(parser => parser.CanParse(filePath));

    public ParsedFile Parse(string filePath, string sourceText)
    {
        var parser = _parsers.FirstOrDefault(p => p.CanParse(filePath))
            ?? throw new InvalidOperationException($"No parser registered for '{filePath}'.");
        return parser.Parse(filePath, sourceText);
    }
}
