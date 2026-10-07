using DevX.Domain;

namespace DevX.Indexer;

/// <summary>Parses JavaScript into ParsedFile by delegating to JavaScriptSymbolReader (DevX.Domain).</summary>
public sealed class JavaScriptParser : ILanguageParser
{
    public bool CanParse(string filePath) => JavaScriptSymbolReader.CanParse(filePath);

    public ParsedFile Parse(string filePath, string sourceText) => JavaScriptSymbolReader.Parse(filePath, sourceText);
}
