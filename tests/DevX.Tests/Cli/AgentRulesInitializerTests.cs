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
    public async Task InitializeAsync_TeachesTheEditStepAndNotJustReading()
    {
        // The v2 arm-B transcripts: the guidance told the agent how to find and read code
        // and never how to change it, so `devx replace` was never used and patch op names
        // were invented. The four steps must be in the product's own onboarding text.
        var root = Directory.CreateTempSubdirectory("devx-init-").FullName;

        await AgentRulesInitializer.InitializeAsync(root, ["codex"], global: false);
        var content = await File.ReadAllTextAsync(Path.Combine(root, "AGENTS.md"));

        Assert.Contains("devx replace <file> --old", content);
        Assert.Contains("--new", content);
        Assert.Contains("devx patch create", content);
        Assert.Contains("The four steps are", content);
        // 1.6.14 item 4: the check is part of the change step (--verify), so edit+check is one call.
        Assert.Contains("--verify", content);
        Assert.Contains("devx verify", content);
    }

    [Fact]
    public async Task InitializeAsync_TeachesBareNamesOutlineAndCrossReferences()
    {
        var root = Directory.CreateTempSubdirectory("devx-init-").FullName;

        await AgentRulesInitializer.InitializeAsync(root, ["codex"], global: false);
        var content = await File.ReadAllTextAsync(Path.Combine(root, "AGENTS.md"));

        Assert.Contains("Bare or `Class.member` names resolve when unique, so do not search first", content);
        Assert.Contains("declaration line with bases plus a member outline", content);
        Assert.Contains("a symbol hit shows the declaration line, with bases for a class", content);
        Assert.Contains("devx callers|callees", content);
        Assert.Contains("devx references", content);
        Assert.Contains("before any `search text`", content);
        Assert.Contains("`read type` (outline) answers 'which methods/fields does X have' -- use `--full` only when you need bodies.", content);
        Assert.Contains("`callers`/`references` work for Python too (text-backed); `callees` does not -- read the body instead.", content);
        Assert.Contains("devx snapshot <file>", content);
        Assert.Contains("--range a:b", content);
        // The block is injected into every agent's context: keep it bounded (OSS24: grew by < 700 bytes).
        var start = content.IndexOf(AgentRulesInitializer.StartMarker, StringComparison.Ordinal);
        var end = content.IndexOf(AgentRulesInitializer.EndMarker, StringComparison.Ordinal) + AgentRulesInitializer.EndMarker.Length;
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(content[start..end]), 1, 3040);
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
