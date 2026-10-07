using DevX.Cli;
using Xunit;

namespace DevX.Tests.Cli;

public sealed class AgentRulesInitializerTests
{
    [Fact]
    public async Task InitializeAsync_AddsRulesWithoutReplacingExistingInstructions()
    {
        var root = Directory.CreateTempSubdirectory("devx-init-").FullName;
        await File.WriteAllTextAsync(Path.Combine(root, "AGENTS.md"), "# Existing rules\n\nKeep this text.\n");

        var updates = await AgentRulesInitializer.InitializeAsync(root, ["codex"], global: false);
        var content = await File.ReadAllTextAsync(Path.Combine(root, "AGENTS.md"));

        Assert.Single(updates);
        Assert.False(updates[0].ReplacedExistingBlock);
        Assert.Contains("Keep this text.", content);
        Assert.Contains(AgentRulesInitializer.StartMarker, content);
        Assert.Contains("Use the session ID your brief gives you; create one only if none was given", content);
        Assert.Contains("use the label your brief gives you", content);
        Assert.Contains("devx search text", content);
        Assert.Contains("--agent codex", content);
    }

    [Fact]
    public async Task InitializeAsync_ReplacesOnlyTheMarkedBlockOnRerun()
    {
        var root = Directory.CreateTempSubdirectory("devx-init-").FullName;
        await AgentRulesInitializer.InitializeAsync(root, ["claude"], global: false);
        var path = Path.Combine(root, "CLAUDE.md");
        await File.AppendAllTextAsync(path, "\n\nUser-owned footer.\n");

        var updates = await AgentRulesInitializer.InitializeAsync(root, ["claude"], global: false);
        var content = await File.ReadAllTextAsync(path);

        Assert.True(updates[0].ReplacedExistingBlock);
        Assert.Equal(1, Count(content, AgentRulesInitializer.StartMarker));
        Assert.Contains("User-owned footer.", content);
    }

    [Fact]
    public async Task InitializeAsync_CreatesBothRepositoryInstructionFilesByDefaultSelection()
    {
        var root = Directory.CreateTempSubdirectory("devx-init-").FullName;

        await AgentRulesInitializer.InitializeAsync(root, [], global: false);

        Assert.True(File.Exists(Path.Combine(root, "CLAUDE.md")));
        Assert.True(File.Exists(Path.Combine(root, "AGENTS.md")));
    }

    private static int Count(string value, string needle)
        => value.Split(needle, StringSplitOptions.None).Length - 1;
}
