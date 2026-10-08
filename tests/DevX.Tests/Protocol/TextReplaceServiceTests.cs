using DevX.Protocol;
using Xunit;

namespace DevX.Tests.Protocol;

public sealed class TextReplaceServiceTests
{
    [Fact] public void Apply_UniqueMatch_ReplacesTextAndReturnsDiff()
    {
        var (root, path) = CreateFile("a.txt", "before old after\n");
        var result = new TextReplaceService().Apply(root, "a.txt", "old", "new");
        Assert.True(result.Success); Assert.Equal(1, result.Replaced); Assert.Equal("before new after\n", File.ReadAllText(path)); Assert.Contains("-old\n+new", result.Diff);
    }

    [Fact] public void Apply_AmbiguousMatch_ReportsCountAndLineNumbersWithoutChangingFile()
    {
        var (root, path) = CreateFile("a.txt", "x\nold\ny old\n");
        var result = new TextReplaceService().Apply(root, "a.txt", "old", "new");
        Assert.False(result.Success); Assert.Contains("2 times", result.Message); Assert.Contains("lines 2, 3", result.Message); Assert.Equal("x\nold\ny old\n", File.ReadAllText(path));
    }

    [Fact] public void Apply_MissingMatch_ReportsFailureAndPreservesFile()
    {
        var (root, path) = CreateFile("a.txt", "unchanged"); var result = new TextReplaceService().Apply(root, "a.txt", "absent", "new");
        Assert.False(result.Success); Assert.Contains("old text not found", result.Message); Assert.Equal("unchanged", File.ReadAllText(path));
    }

    [Fact] public void Apply_All_ReplacesEveryOccurrence()
    {
        var (root, path) = CreateFile("a.txt", "x x x"); var result = new TextReplaceService().Apply(root, "a.txt", "x", "y", replaceAll: true);
        Assert.True(result.Success); Assert.Equal(3, result.Replaced); Assert.Equal("y y y", File.ReadAllText(path));
    }

    [Fact] public void Apply_Occurrence_ReplacesOnlySelectedMatch()
    {
        var (root, path) = CreateFile("a.txt", "x x x"); var result = new TextReplaceService().Apply(root, "a.txt", "x", "y", occurrence: 2);
        Assert.True(result.Success); Assert.Equal("x y x", File.ReadAllText(path));
    }

    [Fact] public void Apply_PathEscape_IsRejectedAndFileIsUnchanged()
    {
        var (root, path) = CreateFile("a.txt", "old"); var result = new TextReplaceService().Apply(root, "../outside.txt", "old", "new");
        Assert.False(result.Success); Assert.Contains("outside the repository root", result.Message); Assert.Equal("old", File.ReadAllText(path));
    }

    [Fact] public void Apply_MultilineText_ReplacesExactBlock()
    {
        var (root, path) = CreateFile("a.txt", "first\nold block\nlast\n"); var result = new TextReplaceService().Apply(root, "a.txt", "old block\n", "new block\nextra\n");
        Assert.True(result.Success); Assert.Equal("first\nnew block\nextra\nlast\n", File.ReadAllText(path)); Assert.Contains("new block", result.Diff);
    }

    private static (string Root, string Path) CreateFile(string name, string content)
    {
        var root = Directory.CreateTempSubdirectory("devx-replace-unit").FullName; var path = Path.Combine(root, name); File.WriteAllText(path, content); return (root, path);
    }
}
