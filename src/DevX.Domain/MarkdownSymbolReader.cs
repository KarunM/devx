using System.Text;

namespace DevX.Domain;

/// <summary>
/// Parses Markdown into the same ParsedFile shape CSharpRoslynParser and PythonSymbolReader
/// produce, so `search symbol`, `snapshot`, `read method` and `patch` work on .md files with no
/// changes to their call sites.
///
/// Mapping, chosen so a heading behaves exactly like a symbol:
///   * a top-level heading (the shallowest level present in the file) becomes a ParsedType,
///   * every deeper heading under it becomes a ParsedMethod on that type,
///   * content before the first heading becomes a synthetic "(preamble)" type.
/// A section's EndLine runs to the line before the next heading at the same or shallower level,
/// so `read method` on a heading returns that whole section including its body -- the Markdown
/// equivalent of reading one function instead of the whole file.
///
/// Implemented natively in C# rather than shelling out to a helper script (the Python route) so
/// .md reads cost no process launch: indexing a docs-heavy repo touches hundreds of files.
/// </summary>
public static class MarkdownSymbolReader
{
    private static readonly string[] Extensions = [".md", ".markdown", ".mdx"];

    public static bool CanParse(string filePath)
        => Extensions.Contains(Path.GetExtension(filePath), StringComparer.OrdinalIgnoreCase);

    public static ParsedFile Parse(string filePath)
    {
        string sourceText;
        try
        {
            sourceText = System.IO.File.ReadAllText(filePath);
        }
        catch (IOException)
        {
            return new ParsedFile(filePath, null, []);
        }

        return Parse(filePath, sourceText);
    }

    public static ParsedFile Parse(string filePath, string sourceText)
    {
        var lines = (sourceText ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        var headings = ExtractHeadings(lines);
        var @namespace = DocumentNamespaceFromPath(filePath);

        if (headings.Count == 0)
        {
            // A heading-less document is still worth indexing: expose the whole file as one
            // section so `snapshot` and `read method` have something to return.
            var whole = new ParsedType(
                DocumentName(filePath),
                "Document",
                @namespace,
                "public",
                1,
                Math.Max(lines.Length, 1),
                [],
                [],
                []);
            return new ParsedFile(filePath, @namespace, [whole]);
        }

        // The shallowest heading level present is the section level. A document whose only
        // headings are `##` still yields top-level sections rather than an empty type list.
        var topLevel = headings.Min(heading => heading.Level);
        var types = new List<ParsedType>();

        // Content above the first heading (front matter, an intro paragraph, a badge block)
        // would otherwise be unreachable through any read path.
        if (headings[0].Line > 1)
        {
            types.Add(new ParsedType(
                "(preamble)",
                "Section",
                @namespace,
                "public",
                1,
                headings[0].Line - 1,
                [],
                [],
                []));
        }

        for (var i = 0; i < headings.Count; i++)
        {
            if (headings[i].Level != topLevel)
            {
                continue;
            }

            var section = headings[i];
            var sectionEnd = SectionEndLine(headings, i, topLevel, lines.Length);

            var methods = new List<ParsedMethod>();
            for (var j = i + 1; j < headings.Count && headings[j].Level > topLevel; j++)
            {
                var sub = headings[j];
                var subEnd = SectionEndLine(headings, j, sub.Level, lines.Length);
                methods.Add(new ParsedMethod(
                    sub.Text,
                    new string('#', sub.Level) + " " + sub.Text,
                    "Section",
                    "public",
                    sub.Line,
                    subEnd,
                    false));
            }

            types.Add(new ParsedType(
                section.Text,
                "Section",
                @namespace,
                "public",
                section.Line,
                sectionEnd,
                methods,
                [],
                []));
        }

        return new ParsedFile(filePath, @namespace, types);
    }

    /// <summary>
    /// A section ends on the line before the next heading at the same or a shallower level;
    /// a deeper heading is part of this section's body, not a sibling that terminates it.
    /// </summary>
    private static int SectionEndLine(IReadOnlyList<Heading> headings, int index, int level, int totalLines)
    {
        for (var next = index + 1; next < headings.Count; next++)
        {
            if (headings[next].Level <= level)
            {
                return Math.Max(headings[next].Line - 1, headings[index].Line);
            }
        }

        return Math.Max(totalLines, headings[index].Line);
    }

    /// <summary>
    /// Recognizes ATX headings (`## Title`) and Setext headings (a line underlined by === or ---),
    /// skipping any heading-looking line inside a fenced code block -- a `# comment` in a shell
    /// snippet is not a section, and treating it as one corrupts every following section's span.
    /// </summary>
    private static List<Heading> ExtractHeadings(string[] lines)
    {
        var headings = new List<Heading>();
        string? fence = null;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            var fenceMarker = FenceMarker(trimmed);
            if (fenceMarker is not null)
            {
                if (fence is null)
                {
                    fence = fenceMarker;
                }
                else if (fenceMarker[0] == fence[0] && fenceMarker.Length >= fence.Length)
                {
                    fence = null;
                }

                continue;
            }

            if (fence is not null)
            {
                continue;
            }

            if (trimmed.StartsWith('#'))
            {
                var level = 0;
                while (level < trimmed.Length && trimmed[level] == '#')
                {
                    level++;
                }

                // `#hashtag` is not a heading; ATX requires a space (or end of line) after the run.
                if (level is >= 1 and <= 6 && (level == trimmed.Length || trimmed[level] == ' ' || trimmed[level] == '\t'))
                {
                    headings.Add(new Heading(level, CleanHeadingText(trimmed[level..]), i + 1));
                }

                continue;
            }

            // Setext: a non-empty line followed by all-= (H1) or all-- (H2).
            if (i + 1 < lines.Length && trimmed.Length > 0)
            {
                var underline = lines[i + 1].Trim();
                if (underline.Length >= 2
                    && (underline.All(c => c == '=') || underline.All(c => c == '-')))
                {
                    headings.Add(new Heading(underline[0] == '=' ? 1 : 2, CleanHeadingText(trimmed), i + 1));
                    i++;
                }
            }
        }

        return headings;
    }

    private static string? FenceMarker(string trimmed)
    {
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return new string('`', trimmed.TakeWhile(c => c == '`').Count());
        }

        if (trimmed.StartsWith("~~~", StringComparison.Ordinal))
        {
            return new string('~', trimmed.TakeWhile(c => c == '~').Count());
        }

        return null;
    }

    /// <summary>
    /// Normalizes heading text to something usable as a symbol name: strips the closing hashes of
    /// a closed ATX heading, unwraps inline code/emphasis markers, and collapses whitespace, so
    /// `## **`build`** run ##` searches as `build run`.
    /// </summary>
    private static string CleanHeadingText(string raw)
    {
        var text = raw.Trim().TrimEnd('#').Trim();
        var builder = new StringBuilder(text.Length);
        var lastWasSpace = false;

        foreach (var c in text)
        {
            if (c is '`' or '*' or '_')
            {
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                lastWasSpace = true;
                continue;
            }

            lastWasSpace = false;
            builder.Append(c);
        }

        var cleaned = builder.ToString().Trim();
        return cleaned.Length == 0 ? "(untitled)" : cleaned;
    }

    private static string DocumentName(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        return string.IsNullOrWhiteSpace(name) ? "(document)" : name;
    }

    /// <summary>
    /// Mirrors PythonSymbolReader.ModuleNamespaceFromPath: Markdown has no namespace of its own, and
    /// the indexing pipeline drops any ParsedFile whose Namespace is blank, so synthesize a dotted
    /// path from the last few path segments (e.g. "docs.guides.setup").
    /// </summary>
    public static string DocumentNamespaceFromPath(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        var withoutExtension = extension.Length > 0 && CanParse(filePath)
            ? filePath[..^extension.Length]
            : filePath;
        var normalized = withoutExtension.Replace('\\', '/').Trim('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return "markdown";
        }

        var take = Math.Min(segments.Length, 3);
        return string.Join('.', segments[^take..]);
    }

    private readonly record struct Heading(int Level, string Text, int Line);
}
