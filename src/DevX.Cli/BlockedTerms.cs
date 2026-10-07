namespace DevX.Cli;

public static class BlockedTerms
{
    public static async Task<IReadOnlyList<string>> FindMatchesAsync(string path, string content)
    {
        var terms = (await File.ReadAllLinesAsync(path))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var matches = new List<string>();
        foreach (var term in terms)
        {
            var index = 0;
            var found = false;
            while ((index = content.IndexOf(term, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                found = true;
                index += term.Length;
            }
            if (found) matches.Add(term);
        }
        return matches;
    }
}
