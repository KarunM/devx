namespace DevX.Cli;

public sealed record SearchReplaceBlock(string Search, string Replace);
public sealed record SearchReplaceParseResult(bool Success, string? ErrorMessage, IReadOnlyList<SearchReplaceBlock> Blocks);

public static class SearchReplaceBlockParser
{
    private const string SearchMarker = "<<<<<<< SEARCH";
    private const string Delimiter = "=======";
    private const string ReplaceMarker = ">>>>>>> REPLACE";

    public static SearchReplaceParseResult Parse(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return new SearchReplaceParseResult(false, "zero_blocks", []);
        }

        var normalized = response.Replace("\r\n", "\n").Replace('\r', '\n');
        if (normalized.Contains("```", StringComparison.Ordinal))
        {
            return new SearchReplaceParseResult(false, "markdown_fence_outside_blocks", []);
        }

        var lines = normalized.Split('\n');
        var blocks = new List<SearchReplaceBlock>();
        var index = 0;

        while (index < lines.Length)
        {
            SkipBlankLines(lines, ref index);
            if (index >= lines.Length)
            {
                break;
            }

            if (!lines[index].Equals(SearchMarker, StringComparison.Ordinal))
            {
                return new SearchReplaceParseResult(false, "prose_outside_blocks", []);
            }

            index++;
            var searchLines = new List<string>();
            var sawDelimiter = false;
            while (index < lines.Length)
            {
                var line = lines[index];
                if (line.Equals(Delimiter, StringComparison.Ordinal))
                {
                    sawDelimiter = true;
                    index++;
                    break;
                }

                if (line.Equals(SearchMarker, StringComparison.Ordinal) || line.Equals(ReplaceMarker, StringComparison.Ordinal))
                {
                    return new SearchReplaceParseResult(false, "missing_replace_end", []);
                }

                if (line.Contains(Delimiter, StringComparison.Ordinal))
                {
                    return new SearchReplaceParseResult(false, "duplicate_delimiter", []);
                }

                searchLines.Add(line);
                index++;
            }

            if (!sawDelimiter)
            {
                return new SearchReplaceParseResult(false, "missing_replace_end", []);
            }

            var replaceLines = new List<string>();
            var sawEnd = false;
            while (index < lines.Length)
            {
                var line = lines[index];
                if (line.Equals(ReplaceMarker, StringComparison.Ordinal))
                {
                    sawEnd = true;
                    index++;
                    break;
                }

                if (line.Equals(SearchMarker, StringComparison.Ordinal))
                {
                    return new SearchReplaceParseResult(false, "prose_outside_blocks", []);
                }

                if (line.Equals(Delimiter, StringComparison.Ordinal))
                {
                    return new SearchReplaceParseResult(false, "duplicate_delimiter", []);
                }

                replaceLines.Add(line);
                index++;
            }

            if (!sawEnd)
            {
                return new SearchReplaceParseResult(false, "missing_replace_end", []);
            }

            blocks.Add(new SearchReplaceBlock(string.Join("\n", searchLines).TrimEnd(), string.Join("\n", replaceLines).TrimEnd()));
        }

        if (blocks.Count == 0)
        {
            return new SearchReplaceParseResult(false, "zero_blocks", []);
        }

        return new SearchReplaceParseResult(true, null, blocks);
    }

    private static void SkipBlankLines(string[] lines, ref int index)
    {
        while (index < lines.Length && string.IsNullOrWhiteSpace(lines[index]))
        {
            index++;
        }
    }
}
