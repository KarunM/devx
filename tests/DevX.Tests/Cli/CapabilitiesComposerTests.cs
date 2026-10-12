using DevX.Cli;
using DevX.Application;
using Xunit;

namespace DevX.Tests.Cli;

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

        Assert.Equal(3, caps.GlobalOptions.Length);

        var formatOpt = caps.GlobalOptions[0];
        Assert.Equal("--format", formatOpt.Name);
        Assert.Equal("Output format (json or text)", formatOpt.Description);
        Assert.Equal("text", formatOpt.Default);
        Assert.False(formatOpt.Required);

        var sessionOpt = caps.GlobalOptions[1];
        Assert.Equal("--session", sessionOpt.Name);
        Assert.Equal("--agent", caps.GlobalOptions[2].Name);
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
            "version", "init", "ask", "index", "search", "session", "artifact", "patch", "build", "test", "verify",
            "diagnostics", "health", "doctor", "snapshot", "method", "type", "callers",
            "callees", "references", "read", "report", "capabilities"
        };

        foreach (var name in expected)
        {
            Assert.Contains(name, commandNames);
        }

        Assert.Equal(expected.Length, caps.Commands.Length);
    }

    [Theory]
    [InlineData("version", "Show the installed DevX CLI version")]
    [InlineData("init", "Install or update marked DevX workflow rules for coding agents")]
    [InlineData("ask", "Ask the LLM to orchestrate code changes via tool calls")]
    [InlineData("index", "Index a repository for discovery and search")]
    [InlineData("search", "Search indexed symbols or full text")]
    [InlineData("session", "Session management")]
    [InlineData("artifact", "Create or manage reusable patch payload artifacts")]
    [InlineData("patch", "Create, preview, validate, apply, or rollback a patch file")]
    [InlineData("health", "Report runtime readiness for the current workspace")]
    [InlineData("doctor", "Provide deeper runtime diagnostics and remediation guidance")]
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
    public void Compose_IndexCommand_HasStatusUpdateAndChangedSubcommands()
    {
        var caps = CapabilitiesComposer.Compose();
        var index = caps.Commands.Single(c => c.Name == "index");

        Assert.Equal(3, index.Subcommands.Length);
        var subNames = index.Subcommands.Select(s => s.Name).ToHashSet();
        Assert.Contains("status", subNames);
        Assert.Contains("update", subNames);
        Assert.Contains("changed", subNames);
    }

    [Fact]
    public void Compose_PatchCommand_HasFiveSubcommands()
    {
        var caps = CapabilitiesComposer.Compose();
        var patch = caps.Commands.Single(c => c.Name == "patch");

        Assert.Equal(5, patch.Subcommands.Length);
        var subNames = patch.Subcommands.Select(s => s.Name).ToHashSet();
        Assert.Contains("create", subNames);
        Assert.Contains("apply", subNames);
        Assert.Contains("preview", subNames);
        Assert.Contains("validate", subNames);
        Assert.Contains("rollback", subNames);
    }

    [Fact]
    public void Compose_ArtifactCommand_HasCreateDeleteAndPathSubcommands()
    {
        var caps = CapabilitiesComposer.Compose();
        var artifact = caps.Commands.Single(c => c.Name == "artifact");

        Assert.Equal(3, artifact.Subcommands.Length);
        var subNames = artifact.Subcommands.Select(s => s.Name).ToHashSet();
        Assert.Contains("create", subNames);
        Assert.Contains("delete", subNames);
        Assert.Contains("path", subNames);
    }

    [Fact]
    public void Compose_ArtifactCommand_IncludesArtifactContentOptions()
    {
        var caps = CapabilitiesComposer.Compose();
        var artifact = caps.Commands.Single(c => c.Name == "artifact");

        Assert.Contains("--content", artifact.Options);
        Assert.Contains("--content-file", artifact.Options);
        Assert.Contains("--content-json", artifact.Options);
        Assert.Contains("--content-stdin", artifact.Options);
    }

    [Fact]
    public void Compose_PatchCreateCommand_IncludesCreateOptions()
    {
        var caps = CapabilitiesComposer.Compose();
        var patch = caps.Commands.Single(c => c.Name == "patch");

        Assert.Contains("--op", patch.Options);
        Assert.Contains("--path", patch.Options);
        Assert.Contains("--append", patch.Options);
        Assert.Contains("--content", patch.Options);
        Assert.Contains("--replacement", patch.Options);
        Assert.Contains("--method", patch.Options);
        Assert.Contains("--position", patch.Options);
        Assert.Contains("--namespace", patch.Options);
        Assert.Contains("--containing-type", patch.Options);
        Assert.Contains("--type-id", patch.Options);
    }

    [Fact]
    public void Compose_ReadCommand_HasMostOptions()
    {
        var caps = CapabilitiesComposer.Compose();
        var read = caps.Commands.Single(c => c.Name == "read");

        Assert.Equal(new[] { "--session", "--range", "--full", "--id", "--query", "--context" }, read.Options);
    }

    [Fact]
    public void Compose_CapabilitiesCommand_HasSelfReferencingOptions()
    {
        var caps = CapabilitiesComposer.Compose();
        var cap = caps.Commands.Single(c => c.Name == "capabilities");

        Assert.Equal(new[] { "--format" }, cap.Options);
        Assert.Equal("devx capabilities [--format json]", cap.Usage);
    }

    [Fact]
    public void Compose_VersionCommand_HasExpectedUsage()
    {
        var caps = CapabilitiesComposer.Compose();
        var version = caps.Commands.Single(c => c.Name == "version");

        Assert.Equal(new[] { "--format" }, version.Options);
        Assert.Equal("devx version [--format json] | devx --version", version.Usage);
    }

    [Fact]
    public void Compose_HealthAndDoctorCommands_RequireNoPositionalArguments()
    {
        var caps = CapabilitiesComposer.Compose();
        var health = caps.Commands.Single(c => c.Name == "health");
        var doctor = caps.Commands.Single(c => c.Name == "doctor");

        Assert.Empty(health.Args);
        Assert.Empty(doctor.Args);
        Assert.Equal(new[] { "--session" }, health.Options);
        Assert.Equal(new[] { "--session" }, doctor.Options);
    }

    [Fact]
    public void Compose_IncludesRuntimeCapabilityDescriptors()
    {
        var health = new RuntimeHealthRecord(
            "Healthy",
            new RuntimeContext("/repo", "/repo", "DevX.sln", "dotnet", "default"),
            [new RuntimeHealthCheck("workspace", "Healthy", "Workspace root is available.")],
            [],
            [],
            []);

        var caps = CapabilitiesComposer.Compose(health);

        Assert.Contains(caps.CapabilityDescriptors, capability => capability.CapabilityId == "patch.apply");
        Assert.Contains(caps.CapabilityDescriptors, capability => capability.CapabilityId == "build");
        Assert.Contains(caps.CapabilityDescriptors, capability => capability.CapabilityId == "test");
        Assert.Equal("dotnet", caps.RuntimeKind);
        Assert.NotNull(caps.DefaultContext);
        Assert.NotEmpty(caps.RuntimeTargets);
    }

    [Fact]
    public void Compose_ExposesMcpProjectionThroughCapabilityDescriptorsOnly()
    {
        var caps = CapabilitiesComposer.Compose();

        var searchCapability = caps.CapabilityDescriptors.Single(capability => capability.CapabilityId == "search");
        var readCapability = caps.CapabilityDescriptors.Single(capability => capability.CapabilityId == "read");
        var reportCapability = caps.CapabilityDescriptors.Single(capability => capability.CapabilityId == "report");

        Assert.Contains("mcp", searchCapability.Interfaces);
        Assert.Contains("mcp", readCapability.Interfaces);
        Assert.Contains("mcp", reportCapability.Interfaces);

        var searchCommand = caps.Commands.Single(command => command.Name == "search");
        var readCommand = caps.Commands.Single(command => command.Name == "read");
        var reportCommand = caps.Commands.Single(command => command.Name == "report");

        Assert.Equal("Search indexed symbols or full text", searchCommand.Description);
        Assert.Equal("Read file, method, or type source", readCommand.Description);
        Assert.Equal("Generate a session report", reportCommand.Description);
        Assert.Contains("Build, test, and verify resolve the repository's session themselves; report requires explicit --session selection.", caps.CompatibilityNotes);
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
