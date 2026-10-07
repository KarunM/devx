using DevX.Domain;
using Xunit;

namespace DevX.Tests.Indexer;

/// <summary>
/// Covers the Markdown heading-to-symbol mapping that lets `search symbol`, `snapshot` and
/// `read method` address one section of a document instead of returning the whole file.
/// </summary>
public sealed class MarkdownSymbolReaderTests
{
    private static ParsedFile Parse(string markdown) => MarkdownSymbolReader.Parse("docs/sample.md", markdown);

    [Theory]
    [InlineData("notes.md")]
    [InlineData("notes.markdown")]
    [InlineData("notes.MD")]
    [InlineData("notes.mdx")]
    public void CanParse_AcceptsMarkdownExtensions(string path) => Assert.True(MarkdownSymbolReader.CanParse(path));

    [Theory]
    [InlineData("app.py")]
    [InlineData("Program.cs")]
    [InlineData("layout.json")]
    public void CanParse_RejectsNonMarkdown(string path) => Assert.False(MarkdownSymbolReader.CanParse(path));

    [Fact]
    public void Parse_TopLevelHeadingsBecomeTypesAndSubheadingsBecomeMethods()
    {
        var parsed = Parse("""
            # Getting Started

            Intro.

            ## Install

            Body.

            ## Configure

            Body.

            # Reference

            Other.
            """);

        Assert.Equal(["Getting Started", "Reference"], parsed.Types.Select(type => type.Name));
        Assert.Equal(["Install", "Configure"], parsed.Types[0].Methods.Select(method => method.Name));
        Assert.Empty(parsed.Types[1].Methods);
    }

    [Fact]
    public void Parse_SectionSpanStopsBeforeNextSiblingHeading()
    {
        var parsed = Parse("""
            # One

            alpha

            ## Nested

            beta

            # Two

            gamma
            """);

        var first = parsed.Types[0];
        Assert.Equal(1, first.StartLine);

        // "# Two" is on line 9, so section One ends on line 8 and absorbs its nested subsection.
        Assert.Equal(8, first.EndLine);
        Assert.Equal(5, first.Methods[0].StartLine);
        Assert.Equal(8, first.Methods[0].EndLine);
    }

    [Fact]
    public void Parse_IgnoresHeadingLookalikesInsideFencedCodeBlocks()
    {
        var parsed = Parse("""
            # Real

            ```sh
            # not a heading
            ## also not a heading
            ```

            ## Actually A Heading

            body
            """);

        Assert.Single(parsed.Types);
        Assert.Equal(["Actually A Heading"], parsed.Types[0].Methods.Select(method => method.Name));
    }

    [Fact]
    public void Parse_ContentBeforeFirstHeadingBecomesPreambleSection()
    {
        var parsed = Parse("""
            Front matter line.

            # Title

            body
            """);

        Assert.Equal("(preamble)", parsed.Types[0].Name);
        Assert.Equal(1, parsed.Types[0].StartLine);
        Assert.Equal(2, parsed.Types[0].EndLine);
    }

    [Fact]
    public void Parse_RecognizesSetextHeadings()
    {
        var parsed = Parse("""
            Title Here
            ==========

            body

            Sub Here
            --------

            more
            """);

        Assert.Equal("Title Here", parsed.Types[0].Name);
        Assert.Equal(["Sub Here"], parsed.Types[0].Methods.Select(method => method.Name));
    }

    [Fact]
    public void Parse_UsesShallowestPresentLevelAsSectionLevel()
    {
        // A document whose headings start at ## must still produce top-level sections.
        var parsed = Parse("""
            ## Alpha

            a

            ## Beta

            b
            """);

        Assert.Equal(["Alpha", "Beta"], parsed.Types.Select(type => type.Name));
    }

    [Fact]
    public void Parse_StripsInlineMarkupAndClosingHashesFromHeadingNames()
    {
        var parsed = Parse("""
            # **Bold** and `code` ##

            body
            """);

        Assert.Equal("Bold and code", parsed.Types[0].Name);
    }

    [Fact]
    public void Parse_HashWithoutSpaceIsNotAHeading()
    {
        var parsed = Parse("""
            # Real

            #hashtag is not a heading

            body
            """);

        Assert.Single(parsed.Types);
        Assert.Empty(parsed.Types[0].Methods);
    }

    [Fact]
    public void Parse_HeadinglessDocumentStillYieldsOneReadableSection()
    {
        var parsed = Parse("just a paragraph\nand another");

        Assert.Single(parsed.Types);
        Assert.Equal("sample", parsed.Types[0].Name);
        Assert.Equal(1, parsed.Types[0].StartLine);
    }

    [Fact]
    public void Parse_SynthesizesNamespaceSoIndexingEmitsSymbols()
    {
        // The indexing pipeline drops any ParsedFile whose Namespace is blank.
        var parsed = Parse("# Title\n\nbody");
        Assert.False(string.IsNullOrWhiteSpace(parsed.Namespace));
        Assert.Equal("docs.sample", parsed.Namespace);
    }

    [Fact]
    public void StructuredSymbolReader_RoutesByExtension()
    {
        Assert.True(StructuredSymbolReader.CanParse("notes.md"));
        Assert.True(StructuredSymbolReader.CanParse("app.py"));
        Assert.False(StructuredSymbolReader.CanParse("Program.cs"));
        Assert.Equal("Markdown", StructuredSymbolReader.LanguageOf("notes.md"));
        Assert.Equal("Python", StructuredSymbolReader.LanguageOf("app.py"));
        Assert.Equal("C#", StructuredSymbolReader.LanguageOf("Program.cs"));
    }
}
