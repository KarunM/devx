using DevX.Domain;
using Xunit;

namespace DevX.Tests.Domain;

public sealed class SectionTargetTests
{
    [Theory]
    [InlineData("docs/deployment/SETUP.md#Post-merge #30 follow-ups", "docs/deployment/SETUP.md", "Post-merge #30 follow-ups")]
    [InlineData("README.md#Install", "README.md", "Install")]
    [InlineData("a/b.markdown#C# notes", "a/b.markdown", "C# notes")]
    [InlineData("docs/x.MD#Heading", "docs/x.MD", "Heading")]
    [InlineData("docs/x.md#", "docs/x.md", "")]
    [InlineData("docs/x.md#See x.md#y", "docs/x.md", "See x.md#y")]
    [InlineData("src/app.py#helper", "src/app.py", "helper")]
    [InlineData("src/app.py#a#b", "src/app.py#a", "b")]
    public void SplitsAtTheFirstHashAfterTheMarkdownPath(string target, string file, string heading)
    {
        var at = SectionTarget.SeparatorIndex(target);
        Assert.Equal((file, heading), (target[..at], target[(at + 1)..]));
    }

    [Fact]
    public void NoHashMeansNoSeparator() => Assert.Equal(-1, SectionTarget.SeparatorIndex("docs/x.md"));
}
