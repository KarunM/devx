using Xunit;
using Ecp.Cli;

namespace Ecp.Tests.Cli;

public class SearchReplaceBlockParserTests
{
    [Fact]
    public void Parse_ValidSingleBlock_Succeeds()
    {
        var response = """
<<<<<<< SEARCH
old text
=======
new text
>>>>>>> REPLACE
""";

        var result = SearchReplaceBlockParser.Parse(response);

        Assert.True(result.Success);
        Assert.Null(result.ErrorMessage);
        Assert.Single(result.Blocks);
        Assert.Equal("old text", result.Blocks[0].Search);
        Assert.Equal("new text", result.Blocks[0].Replace);
    }

    [Fact]
    public void Parse_ValidTwoBlocks_Succeeds()
    {
        var response = """
<<<<<<< SEARCH
old one
=======
new one
>>>>>>> REPLACE
<<<<<<< SEARCH
old two
=======
new two
>>>>>>> REPLACE
""";

        var result = SearchReplaceBlockParser.Parse(response);

        Assert.True(result.Success);
        Assert.Null(result.ErrorMessage);
        Assert.Equal(2, result.Blocks.Count);
        Assert.Equal("old one", result.Blocks[0].Search);
        Assert.Equal("new one", result.Blocks[0].Replace);
        Assert.Equal("old two", result.Blocks[1].Search);
        Assert.Equal("new two", result.Blocks[1].Replace);
    }

    [Fact]
    public void Parse_ProseOutsideBlock_Fails()
    {
        var response = """
Please update this.
<<<<<<< SEARCH
old text
=======
new text
>>>>>>> REPLACE
""";

        var result = SearchReplaceBlockParser.Parse(response);

        Assert.False(result.Success);
        Assert.Equal("prose_outside_blocks", result.ErrorMessage);
    }

    [Fact]
    public void Parse_DuplicateDelimiter_Fails()
    {
        var response = """
<<<<<<< SEARCH
old text
=======
extra separator
=======
new text
>>>>>>> REPLACE
""";

        var result = SearchReplaceBlockParser.Parse(response);

        Assert.False(result.Success);
        Assert.Equal("duplicate_delimiter", result.ErrorMessage);
    }

    [Fact]
    public void Parse_ZeroBlocks_Fails()
    {
        var result = SearchReplaceBlockParser.Parse(string.Empty);

        Assert.False(result.Success);
        Assert.Equal("zero_blocks", result.ErrorMessage);
    }
}
