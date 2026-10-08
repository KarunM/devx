using Xunit;

namespace DevX.Tests.Cli;

/// <summary>
/// OSS11 item 1. RULES_B puts --session/--agent/--task on every call, and on 1.6.10 that
/// turned `devx --help` into "Unknown command '--session'"; even when it worked, the cheat
/// sheet named `devx patch` but never `devx replace`, so agents guessed at op names.
/// </summary>
public sealed class HelpDiscoverabilityTests : CliHarness
{
    [Fact]
    public async Task AgentHelp_IsReachedWhenTheGlobalFlagsComeFirst()
    {
        // The exact 1.6.10 regression: RULES_B puts --session/--agent/--task on every call,
        // and this argument order answered "Unknown command '--session'".
        var root = NewRoot();
        await WithRoot(root, async r =>
        {
            var (code, output) = await RunAsync(r, "--session", Guid0, "--agent", "codex",
                "--task", "v1", "--help");
            Assert.Equal(0, code);
            Assert.Contains("devx replace <file> --old <text> --new <text>", output);
            Assert.DoesNotContain("Unknown command", output);
        });
    }

    [Fact]
    public async Task AgentHelp_IsReachedWhenTheGlobalFlagsComeLast()
    {
        var root = NewRoot();
        await WithRoot(root, async r =>
        {
            var (code, output) = await RunAsync(r, "--help", "--session", Guid0,
                "--agent", "codex", "--task", "v1");
            Assert.Equal(0, code);
            Assert.Contains("devx replace <file> --old <text> --new <text>", output);
        });
    }

    [Fact]
    public async Task AgentCheatSheet_ListsEveryEditEntryPoint()
    {
        var root = NewRoot();
        await WithRoot(root, async r =>
        {
            var (code, output) = await RunAsync(r, "--help", "--agent", "codex");
            Assert.Equal(0, code);
            Assert.Contains("devx replace", output);
            Assert.Contains("devx patch create|validate|apply", output);
            Assert.Contains("--old <text> --new <text>", output);
            Assert.Contains("--all | --occurrence n", output);
        });
    }

    [Fact]
    public async Task HumanHelp_IsUnchanged()
    {
        var root = NewRoot();
        await WithRoot(root, async r =>
        {
            var (code, output) = await RunAsync(r, "--help");
            Assert.Equal(0, code);
            Assert.Contains("1. devx search <name>", output);
            Assert.Contains("3. devx replace <file> --old <text> --new <text>", output);
            Assert.DoesNotContain("devx cheat sheet", output);
        });
    }

    [Fact]
    public async Task ASearchQueryIsNotAChanceToPrintHelp()
    {
        // A query is argument text, not a subcommand: --help after it must not print the
        // cheat sheet, or every search would answer with the manual.
        var root = NewRoot();
        await WithRoot(root, async r =>
        {
            var (code, output) = await RunAsync(r, "search", "Foo --help", "--session", Guid0);
            Assert.Equal(1, code);
            Assert.DoesNotContain("devx cheat sheet", output);
            Assert.DoesNotContain("1. devx search <name>", output);
        });
    }
}
