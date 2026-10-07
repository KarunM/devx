using DevX.Domain;

namespace DevX.Indexer;

/// <summary>
/// Parses Markdown into the same ParsedFile shape the C# and Python parsers produce, by delegating
/// to MarkdownSymbolReader (DevX.Domain). Lives alongside PythonAstParser so the shared reader backs
/// both indexing and the live re-parse paths in DevX.Infrastructure (read method / snapshot).
/// </summary>
public sealed class MarkdownParser : ILanguageParser
{
    public bool CanParse(string filePath) => MarkdownSymbolReader.CanParse(filePath);

    public ParsedFile Parse(string filePath, string sourceText) => MarkdownSymbolReader.Parse(filePath, sourceText);
}
