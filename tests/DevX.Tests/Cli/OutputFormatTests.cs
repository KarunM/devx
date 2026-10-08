using System.Collections;
using DevX.Cli;
using Xunit;

namespace DevX.Tests.Cli;

/// <summary>
/// 1.6.7: the CLI default must be the lean agent format when an agent is on the other end,
/// because the benchmark arm calls the CLI without --format and so never saw it -- 110,795
/// bytes for `devx search Async` against 926.
///
/// The rule takes its environment as an argument, so every combination is tested directly
/// rather than by trying to fake a pty.
/// </summary>
public sealed class OutputFormatTests
{
    private static Hashtable Environment(params string[] names)
    {
        var table = new Hashtable();
        foreach (var name in names) table[name] = "1";
        return table;
    }

    [Fact]
    public void PipedWithNoFlag_IsAgentFormat() =>
        Assert.Equal("agent", OutputFormat.Resolve(null, looksLikeAnAgent: true));

    [Fact]
    public void TerminalWithNoFlag_IsStillText() =>
        Assert.Equal("text", OutputFormat.Resolve(null, looksLikeAnAgent: false));

    [Theory]
    [InlineData("json")]
    [InlineData("text")]
    [InlineData("agent")]
    public void AnExplicitFormatAlwaysWins(string explicitFormat)
    {
        // Byte-identical to 1.6.6 for both directions: an explicit --format json from a pipe
        // must not be turned into agent, and an explicit --format text from a terminal must
        // not be turned into agent either.
        Assert.Equal(explicitFormat, OutputFormat.Resolve(explicitFormat, looksLikeAnAgent: true));
        Assert.Equal(explicitFormat, OutputFormat.Resolve(explicitFormat, looksLikeAnAgent: false));
    }

    [Theory]
    [InlineData("CODEX_SANDBOX")]
    [InlineData("CLAUDECODE")]
    [InlineData("CLAUDE_CODE_ENTRYPOINT")]
    [InlineData("CURSOR_TRACE_ID")]
    [InlineData("AIDER_MODEL")]
    [InlineData("AI_AGENT")]
    public void AnAgentMarkerInTheEnvironment_IsAnAgentEvenOnATerminal(string marker) =>
        Assert.True(OutputFormat.LooksLikeAnAgent(Environment(marker), outputRedirected: false));

    [Theory]
    [InlineData("PATH")]
    [InlineData("HOME")]
    [InlineData("TERM")]
    [InlineData("CODEXTER")]
    [InlineData("AGENTIC")]
    public void AnOrdinaryVariableIsNotAMarker(string name) =>
        Assert.False(OutputFormat.LooksLikeAnAgent(Environment(name), outputRedirected: false));

    [Fact]
    public void TheRedirectIsEnoughOnItsOwn()
    {
        Assert.True(OutputFormat.LooksLikeAnAgent(new Hashtable(), outputRedirected: true));
        Assert.False(OutputFormat.LooksLikeAnAgent(new Hashtable(), outputRedirected: false));
    }
}
