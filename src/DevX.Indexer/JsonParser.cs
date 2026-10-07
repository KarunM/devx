using DevX.Domain;

namespace DevX.Indexer;

public sealed class JsonParser : ILanguageParser
{
    public bool CanParse(string filePath) => JsonSymbolReader.CanParse(filePath);
    public ParsedFile Parse(string filePath, string sourceText) => JsonSymbolReader.Parse(filePath, sourceText);
}
