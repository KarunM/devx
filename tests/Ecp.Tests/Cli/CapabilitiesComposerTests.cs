using Ecp.Cli;
using Xunit;

namespace Ecp.Tests.Cli;

public sealed class CapabilitiesComposerTests
{
    [Fact]
    public void Compose_ReturnsExpectedVersion()
    {
        var caps = CapabilitiesComposer.Compose();

        Assert.Equal("1.0", caps.Version);
    }

    [Fact]
    public void Compose_ReturnsExpectedGlobalOptions()
    {
        var caps = CapabilitiesComposer.Compose();

        Assert.Equal(2, caps.GlobalOptions.Length);

        var formatOpt = caps.GlobalOptions[0];
        Assert.Equal("--format", formatOpt.Name);
        Assert.Equal("Output format (json or text)", formatOpt.Description);
        Assert.Equal("text", formatOpt.Default);
        Assert.False(formatOpt.Required);

        var sessionOpt = caps.GlobalOptions[1];
        Assert.Equal("--session", sessionOpt.Name);
        Assert.Equal("Session identifier for session-scoped commands", sessionOpt.Description);
        Assert.Null(sessionOpt.Default);
        Assert.False(sessionOpt.Required);
    }

    [Fact]
    public void Compose_ContainsAllKnownCommands()
    {
        var caps = CapabilitiesComposer.Compose();
        var commandNames = caps.Commands.Select(c => c.Name).ToHashSet();

        var expected = new[]
        {
            "ask", "index", "search", "session", "patch", "build", "test",
            "diagnostics", "snapshot", "method", "type", "callers",
            "callees", "references", "read", "report", "capabilities"
        };

        foreach (var name in expected)
        {
            Assert.Contains(name, commandNames);
        }

        Assert.Equal(expected.Length, caps.Commands.Length);
    }

    [Theory]
    [InlineData("ask", "Ask the LLM to orchestrate code changes via tool calls")]
    [InlineData("index", "Index a repository for discovery and search")]
    [InlineData("search", "Search indexed symbols or full text")]
    [InlineData("session", "Session management")]
    [InlineData("patch", "Apply, preview, validate, or rollback a patch file")]
    [InlineData("build", "Build the indexed solution")]
    [InlineData("test", "Run tests for the indexed solution")]
    [InlineData("diagnostics", "Get diagnostics for the indexed solution")]
    [InlineData("snapshot", "Get a structural snapshot of a file")]
    [InlineData("method", "Get method summary or full details")]
    [InlineData("type", "Get type information")]
    [InlineData("callers", "Get callers of a method")]
    [InlineData("callees", "Get callees of a method")]
    [InlineData("references", "Get references to a symbol")]
    [InlineData("read", "Read file, method, or type source")]
    [InlineData("report", "Generate a session report")]
    [InlineData("capabilities", "List all available commands and options in JSON")]
    public void Compose_EachCommand_HasCorrectDescription(string commandName, string expectedDescription)
    {
        var caps = CapabilitiesComposer.Compose();
        var cmd = caps.Commands.Single(c => c.Name == commandName);

        Assert.Equal(expectedDescription, cmd.Description);
        Assert.False(string.IsNullOrWhiteSpace(cmd.Usage));
        Assert.NotNull(cmd.Subcommands);
        Assert.NotNull(cmd.Args);
        Assert.NotNull(cmd.Options);
    }

    [Fact]
    public void Compose_IndexCommand_HasStatusAndUpdateSubcommands()
    {
        var caps = CapabilitiesComposer.Compose();
        var index = caps.Commands.Single(c => c.Name == "index");

        Assert.Equal(2, index.Subcommands.Length);
        var subNames = index.Subcommands.Select(s => s.Name).ToHashSet();
        Assert.Contains("status", subNames);
        Assert.Contains("update", subNames);
    }

    [Fact]
    public void Compose_PatchCommand_HasFourSubcommands()
    {
        var caps = CapabilitiesComposer.Compose();
        var patch = caps.Commands.Single(c => c.Name == "patch");

        Assert.Equal(4, patch.Subcommands.Length);
        var subNames = patch.Subcommands.Select(s => s.Name).ToHashSet();
        Assert.Contains("apply", subNames);
        Assert.Contains("preview", subNames);
        Assert.Contains("validate", subNames);
        Assert.Contains("rollback", subNames);
    }

    [Fact]
    public void Compose_ReadCommand_HasMostOptions()
    {
        var caps = CapabilitiesComposer.Compose();
        var read = caps.Commands.Single(c => c.Name == "read");

        Assert.Equal(new[] { "--session", "--range", "--full", "--id", "--query" }, read.Options);
    }

    [Fact]
    public void Compose_CapabilitiesCommand_HasSelfReferencingOptions()
    {
        var caps = CapabilitiesComposer.Compose();
        var cap = caps.Commands.Single(c => c.Name == "capabilities");

        Assert.Equal(new[] { "--format" }, cap.Options);
        Assert.Equal("ecp capabilities [--format json]", cap.Usage);
    }

    [Fact]
    public void Compose_SessionCommand_HasCreateSubcommandAndNoOptions()
    {
        var caps = CapabilitiesComposer.Compose();
        var session = caps.Commands.Single(c => c.Name == "session");

        Assert.Single(session.Subcommands);
        Assert.Equal("create", session.Subcommands[0].Name);
        Assert.Empty(session.Options);
        Assert.Empty(session.Args);
    }

    [Fact]
    public void Compose_NoCommandHasNullArrays()
    {
        var caps = CapabilitiesComposer.Compose();

        foreach (var cmd in caps.Commands)
        {
            Assert.NotNull(cmd.Subcommands);
            Assert.NotNull(cmd.Args);
            Assert.NotNull(cmd.Options);
        }
    }
}
