namespace DevX.Domain;

/// <summary>Indexes HTML as a readable document so snapshots, content search, and incremental updates work.</summary>
public static class HtmlSymbolReader
{
    public static bool CanParse(string filePath)
        => Path.GetExtension(filePath) is ".html" or ".htm"
            || Path.GetExtension(filePath).Equals(".HTML", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(filePath).Equals(".HTM", StringComparison.OrdinalIgnoreCase);

    public static ParsedFile Parse(string filePath)
        => Parse(filePath, System.IO.File.ReadAllText(filePath));

    public static ParsedFile Parse(string filePath, string sourceText)
    {
        var lines = Math.Max(1, sourceText.Replace("\r\n", "\n").Split('\n').Length);
        var name = Path.GetFileNameWithoutExtension(filePath);
        return new ParsedFile(filePath, "html",
        [
            new ParsedType(name, "Document", "html", "public", 1, lines, [], [], [])
        ]);
    }
}
