using System.Text.RegularExpressions;

namespace DevX.Domain;

/// <summary>
/// Splits a <c>file#Heading</c> section target. The path ends at a Markdown extension, so the
/// separator is the first '#' after it and a heading may itself contain '#'
/// ("Post-merge #30 follow-ups"). Targets with no Markdown extension keep the older rule:
/// the last '#'.
/// </summary>
public static class SectionTarget
{
    private static readonly Regex MarkdownPath = new(@"\.(?:md|markdown|mdx)#", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Index of the '#' that separates path from heading, or -1 when there is none.</summary>
    public static int SeparatorIndex(string target)
    {
        var match = MarkdownPath.Match(target);
        return match.Success ? match.Index + match.Length - 1 : target.LastIndexOf('#');
    }
}
