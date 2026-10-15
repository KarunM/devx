namespace DevX.Domain;

/// <summary>Shared helpers for the lightweight Terraform / YAML / TOML readers.</summary>
internal static class ConfigSymbolSupport
{
    /// <summary>Dotted namespace from the last three path segments (extension dropped, leading dots trimmed).
    /// Never blank: the indexing pipeline drops a ParsedFile whose namespace is blank.</summary>
    public static string NamespaceFromPath(string filePath, string fallback)
    {
        var extension = Path.GetExtension(filePath);
        var withoutExtension = extension.Length > 0 ? filePath[..^extension.Length] : filePath;
        var segments = withoutExtension.Replace('\\', '/').Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => segment.Trim('.'))
            .Where(segment => segment.Length > 0)
            .ToArray();
        if (segments.Length == 0) return fallback;
        return string.Join('.', segments[^Math.Min(segments.Length, 3)..]);
    }

    public static string[] SplitLines(string? text)
    {
        var lines = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        return lines.Length > 1 && lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    /// <summary>Last non-blank line index (1-based) in [from, to], so a block's span does not swallow trailing blank lines.</summary>
    public static int TrimTrailingBlank(string[] lines, int from, int to)
    {
        while (to > from && string.IsNullOrWhiteSpace(lines[to - 1])) to--;
        return to;
    }
}
