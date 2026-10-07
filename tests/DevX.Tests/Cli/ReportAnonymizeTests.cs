using System.Text.Json;
using DevX.Cli;
using Xunit;

namespace DevX.Tests.Cli;

[Collection("ProcessGlobalState")]
public sealed class ReportAnonymizeTests
{
    [Fact]
    public void Anonymizer_ReplacesIdentifyingLabelsAndPreservesTotalsAcrossRenders()
    {
        var input = Fixture();
        var report = AnalyticsReportAggregator.Compose(input, DateTimeOffset.UtcNow, TimeSpan.FromDays(30));
        var anonymized = ReportAnonymizer.Anonymize(report);
        var html = AnalyticsHtmlRenderer.Render(anonymized);
        var json = JsonSerializer.Serialize(anonymized);
        var second = JsonSerializer.Serialize(ReportAnonymizer.Anonymize(report));

        foreach (var original in new[] { "src/Secret/Widget.cs", "Secret.Widget.Frobnicate", "SecretRepo", "task-opaque" })
        {
            Assert.DoesNotContain(original, html);
            Assert.DoesNotContain(original, json);
        }
        Assert.Contains("repo-", json);
        Assert.Contains("file-", json);
        Assert.Contains("symbol-", json);
        Assert.Contains("task-", json);
        Assert.Equal(json, second);
        Assert.Equal(report.Operations, anonymized.Operations);
        Assert.Equal(report.ResponseCharacters, anonymized.ResponseCharacters);
        Assert.Equal(report.Repositories, anonymized.Repositories);
    }

    [Fact]
    public async Task BlockedTerms_NoMatchWritesReportWithSuccess()
    {
        var paths = Paths();
        try
        {
            await File.WriteAllTextAsync(paths.Input, JsonSerializer.Serialize(Fixture()));
            await File.WriteAllTextAsync(paths.Terms, "# ignored\n\nzzterm-one\n");
            var result = await AnalyticsReportCommand.RunAsync("unused", Args(paths, paths.Terms));
            Assert.Equal(0, result);
            Assert.True(File.Exists(paths.Output));
        }
        finally { Cleanup(paths); }
    }

    [Fact]
    public async Task BlockedTerms_MatchReturnsThreeAndDoesNotPrintTermOrWriteReport()
    {
        var paths = Paths();
        var previous = Console.Error;
        using var captured = new StringWriter();
        try
        {
            await File.WriteAllTextAsync(paths.Input, JsonSerializer.Serialize(Fixture("zzterm-one")));
            await File.WriteAllTextAsync(paths.Terms, "ZZTERM-ONE\n");
            Console.SetError(captured);
            var result = await AnalyticsReportCommand.RunAsync("unused", Args(paths, paths.Terms));
            Assert.Equal(3, result);
            Assert.Contains("blocked terms found: 1 (1 terms)", captured.ToString());
            Assert.False(captured.ToString().Contains("zzterm-one", StringComparison.OrdinalIgnoreCase));
            Assert.False(File.Exists(paths.Output));
        }
        finally { Console.SetError(previous); Cleanup(paths); }
    }

    [Fact]
    public async Task BlockedTerms_MissingFileReturnsTwoWithoutWritingReport()
    {
        var paths = Paths();
        var previous = Console.Error;
        using var captured = new StringWriter();
        try
        {
            await File.WriteAllTextAsync(paths.Input, JsonSerializer.Serialize(Fixture()));
            Console.SetError(captured);
            var result = await AnalyticsReportCommand.RunAsync("unused", Args(paths, paths.Terms));
            Assert.Equal(2, result);
            Assert.Contains("Could not read blocked-terms file", captured.ToString());
            Assert.False(File.Exists(paths.Output));
        }
        finally { Console.SetError(previous); Cleanup(paths); }
    }

    private static AnalyticsInput Fixture(string organization = "SecretRepo")
    {
        var now = DateTimeOffset.UtcNow;
        var metadata = JsonSerializer.Serialize(new { AttemptedName = "Secret.Widget.Frobnicate", ResponseCharacters = 420 });
        var evt = new AnalyticsEvent("dummy-event", "SecretRepo", null, null, "dummy-agent", "dummy-session",
            "read method", now.AddHours(-1), false, 12, metadata, "/workspace/SecretRepo", "task-opaque");
        var health = new IndexHealthSnapshot("/workspace/SecretRepo", now, 1, 0, 0,
            ["src/Secret/Widget.cs"], []);
        return new AnalyticsInput(organization, [evt], [health]);
    }

    private static (string Input, string Terms, string Output) Paths()
    {
        var root = Directory.CreateTempSubdirectory("devx-anon-").FullName;
        return (Path.Combine(root, "input.json"), Path.Combine(root, "terms.txt"), Path.Combine(root, "report.html"));
    }

    private static string[] Args((string Input, string Terms, string Output) paths, string terms)
        => ["report", "html", "--input", paths.Input, "--output", paths.Output, "--blocked-terms", terms];

    private static void Cleanup((string Input, string Terms, string Output) paths)
    {
        var directory = Path.GetDirectoryName(paths.Input)!;
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
