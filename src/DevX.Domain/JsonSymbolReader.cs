using System.Text.Json;

namespace DevX.Domain;

/// <summary>
/// Makes JSON files first-class indexed files. Top-level object properties are exposed as fields,
/// while the document itself is exposed as a type so snapshots remain useful even for arrays or
/// temporarily invalid JSON.
/// </summary>
public static class JsonSymbolReader
{
    public static bool CanParse(string filePath)
        => Path.GetExtension(filePath).Equals(".json", StringComparison.OrdinalIgnoreCase);

    public static ParsedFile Parse(string filePath)
        => Parse(filePath, System.IO.File.ReadAllText(filePath));

    public static ParsedFile Parse(string filePath, string sourceText)
    {
        var lineCount = Math.Max(1, sourceText.Replace("\r\n", "\n").Split('\n').Length);
        var fields = new List<ParsedField>();
        try
        {
            using var document = JsonDocument.Parse(sourceText, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    fields.Add(new ParsedField(property.Name, property.Value.ValueKind.ToString(), "public", 1, lineCount));
                }
            }
        }
        catch (JsonException)
        {
            // Still index the file itself. Snapshot should not fail merely because an editor has
            // left JSON temporarily incomplete.
        }

        var name = Path.GetFileNameWithoutExtension(filePath);
        // JSON has no language namespace. A stable short namespace avoids leaking absolute
        // workspace paths into qualified names and keeps lookups consistent across machines.
        const string @namespace = "json";

        return new ParsedFile(filePath, @namespace,
        [
            new ParsedType(name, "Document", @namespace, "public", 1, lineCount, [], [], fields)
        ]);
    }
}
