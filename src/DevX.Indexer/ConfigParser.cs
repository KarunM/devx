using DevX.Domain;

namespace DevX.Indexer;

/// <summary>Parses Terraform/HCL, YAML and TOML into ParsedFile through the lightweight readers in DevX.Domain.</summary>
public sealed class ConfigParser : ILanguageParser
{
    public bool CanParse(string filePath) => StructuredSymbolReader.ConfigFile(filePath);

    public ParsedFile Parse(string filePath, string sourceText)
        => HclSymbolReader.CanParse(filePath) ? HclSymbolReader.Parse(filePath, sourceText)
            : YamlSymbolReader.CanParse(filePath) ? YamlSymbolReader.Parse(filePath, sourceText)
            : TomlSymbolReader.Parse(filePath, sourceText);
}
