using Xunit;
using File = System.IO.File;

namespace DevX.Tests.Cli;

/// <summary>
/// OSS11 item 3. `devx ask` used to fall back to http://localhost:8080 when the repo had no
/// LLM configured, so an agent spent a call and a wait learning only that something at
/// localhost was not listening. An unconfigured `ask` now refuses immediately with the fix.
/// </summary>
public sealed class AskConfigurationTests : CliHarness
{
    private const string Config =
        """{"llm":{"provider":"ollama","model":"m","endpoint":"http://127.0.0.1:9"}}""";

    [Fact]
    public async Task Ask_UnconfiguredFailsFastAndNeverMentionsLocalhost()
    {
        var root = NewRoot();
        await WithRoot(root, async r =>
        {
            var started = DateTime.UtcNow;
            var (code, output) = await RunAsync(r, "ask", "how does the index work?");
            var elapsed = DateTime.UtcNow - started;
            Assert.Equal(1, code);
            Assert.Contains("devx ask is not configured (set llm.endpoint in .devx/config.json)",
                output);
            Assert.DoesNotContain("localhost", output);
            Assert.DoesNotContain("8080", output);
            Assert.True(elapsed < TimeSpan.FromSeconds(1), $"took {elapsed.TotalSeconds:F2}s");
        });
    }

    [Fact]
    public async Task Ask_IsNotAdvertisedWhenUnconfiguredAndIsWhenConfigured()
    {
        var root = NewRoot();
        await WithRoot(root, async r =>
        {
            var (_, unconfigured) = await RunAsync(r, "--help");
            Assert.DoesNotContain("devx ask", unconfigured);

            Directory.CreateDirectory(Path.Combine(r, ".devx"));
            await File.WriteAllTextAsync(Path.Combine(r, ".devx", "config.json"), Config);

            var (_, configured) = await RunAsync(r, "--help");
            Assert.Contains("devx ask", configured);
        });
    }

    [Fact]
    public async Task Ask_ConfiguredDoesNotTakeTheUnconfiguredPath()
    {
        var root = NewRoot();
        await WithRoot(root, async r =>
        {
            Directory.CreateDirectory(Path.Combine(r, ".devx"));
            await File.WriteAllTextAsync(Path.Combine(r, ".devx", "config.json"), Config);
            var (_, output) = await RunAsync(r, "ask", "hi");
            Assert.DoesNotContain("is not configured", output);
        });
    }

    [Fact]
    public async Task HelpIsNotHijackedByAPromptThatMentionsIt()
    {
        // `devx ask "explain --help"` is a question about the flag, not a help request. It
        // must be dispatched as a question (and refused here for want of a config), never
        // answered with the cheat sheet.
        var root = NewRoot();
        await WithRoot(root, async r =>
        {
            var (code, output) = await RunAsync(r, "ask", "explain --help");
            Assert.Equal(1, code);
            Assert.Contains("devx ask is not configured", output);
            Assert.DoesNotContain("devx cheat sheet", output);
        });
    }
}
