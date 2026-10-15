namespace DevX.Domain;

/// <summary>
/// Single dispatch point for the non-Roslyn symbol readers (Python, Markdown). Every site that
/// previously asked `PythonSymbolReader.CanParse(path)` to mean "is this a file Roslyn cannot
/// handle but we can still parse?" asks this instead, so adding a language touches one file
/// rather than every read, snapshot, patch and index path.
/// </summary>
public static class StructuredSymbolReader
{
    public static bool CanParse(string filePath)
        => PythonSymbolReader.CanParse(filePath) || MarkdownSymbolReader.CanParse(filePath)
            || JavaScriptSymbolReader.CanParse(filePath) || JsonSymbolReader.CanParse(filePath)
            || HtmlSymbolReader.CanParse(filePath) || ConfigFile(filePath);

    /// <summary>Terraform/HCL, YAML and TOML: the lightweight config readers, indexed under one "&lt;config&gt;" project.</summary>
    public static bool ConfigFile(string filePath)
        => HclSymbolReader.CanParse(filePath) || YamlSymbolReader.CanParse(filePath) || TomlSymbolReader.CanParse(filePath);

    public static ParsedFile Parse(string filePath)
        => MarkdownSymbolReader.CanParse(filePath)
            ? MarkdownSymbolReader.Parse(filePath)
            : HtmlSymbolReader.CanParse(filePath)
                ? HtmlSymbolReader.Parse(filePath)
            : JsonSymbolReader.CanParse(filePath)
                ? JsonSymbolReader.Parse(filePath)
            : HclSymbolReader.CanParse(filePath)
                ? HclSymbolReader.Parse(filePath)
            : YamlSymbolReader.CanParse(filePath)
                ? YamlSymbolReader.Parse(filePath)
            : TomlSymbolReader.CanParse(filePath)
                ? TomlSymbolReader.Parse(filePath)
            : JavaScriptSymbolReader.CanParse(filePath)
                ? JavaScriptSymbolReader.Parse(filePath)
                : PythonSymbolReader.Parse(filePath);

    /// <summary>
    /// The language label reported on read results, so a caller can tell a Markdown section from a
    /// Python function without re-inspecting the path.
    /// </summary>
    private static bool IsTypeScript(string filePath)
        => Path.GetExtension(filePath).ToLowerInvariant() is ".ts" or ".tsx" or ".mts" or ".cts";

    public static string LanguageOf(string filePath)
        => MarkdownSymbolReader.CanParse(filePath) ? "Markdown"
            : HtmlSymbolReader.CanParse(filePath) ? "HTML"
            : JsonSymbolReader.CanParse(filePath) ? "JSON"
            : HclSymbolReader.CanParse(filePath) ? "Terraform"
            : YamlSymbolReader.CanParse(filePath) ? "YAML"
            : TomlSymbolReader.CanParse(filePath) ? "TOML"
            : JavaScriptSymbolReader.CanParse(filePath) ? (IsTypeScript(filePath) ? "TypeScript" : "JavaScript")
            : PythonSymbolReader.CanParse(filePath) ? "Python"
            : "C#";
}
