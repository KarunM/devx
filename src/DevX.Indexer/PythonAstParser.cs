using DevX.Domain;

namespace DevX.Indexer;

/// <summary>
/// Parses Python source into the same ParsedFile shape CSharpRoslynParser produces, by delegating to
/// PythonSymbolReader (DevX.Domain), which shells out to a `python3 -c ast`-style helper script.
/// Chosen over a tree-sitter-python native binding because it needs no new native/NuGet dependency
/// and Python's own ast module is always correct for the Python interpreter actually installed on
/// the box. The shared reader also backs the live re-parse paths in DevX.Infrastructure
/// (read method / snapshot / type) so both indexing and reads use identical symbol extraction.
/// </summary>
public sealed class PythonAstParser : ILanguageParser
{
    public bool CanParse(string filePath) => PythonSymbolReader.CanParse(filePath);

    public ParsedFile Parse(string filePath, string sourceText) => PythonSymbolReader.Parse(filePath);
}
