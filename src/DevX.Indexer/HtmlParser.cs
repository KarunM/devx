using DevX.Domain;

namespace DevX.Indexer;

public sealed class HtmlParser : ILanguageParser
{
    public bool CanParse(string filePath) => HtmlSymbolReader.CanParse(filePath);
    public ParsedFile Parse(string filePath, string sourceText) => HtmlSymbolReader.Parse(filePath, sourceText);
}
