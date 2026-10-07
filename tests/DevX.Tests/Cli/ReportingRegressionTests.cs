using System.Diagnostics;
using System.Text.Json;
using DevX.Cli;
using DevX.Domain;
using DevX.Indexer;
using DevX.Infrastructure.Storage.SQLite;
using Microsoft.Data.Sqlite;
using Xunit;
using File = System.IO.File;

namespace DevX.Tests.Cli;

public sealed class ReportingRegressionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-07T12:00:00Z");
    private static AnalyticsEvent E(string id, string operation = "read method", bool success = true, object? metadata = null,
        string session = "s1", string agent = "codex", string? task = null, double ms = 10, string workspace = "live3")
        => new(id, "project", null, null, agent, session, operation, Now.AddHours(-1), success, ms,
            metadata is null ? null : JsonSerializer.Serialize(metadata), workspace, task);
    private static ReportingDetails Details(params AnalyticsEvent[] events)
        => AnalyticsReportAggregator.Compose(new(null, events), Now, TimeSpan.FromDays(1)).Reporting!;

    [Fact]
    public void FailureAnalysis_ExactCountsNamesRepeatsAndLegacyFailures()
    {
        var d = Details(E("1", success: false, metadata: new { ErrorCode = "NotFound", FailureReason = "missing", AttemptedName = "a", Suggestions = new[] { "b" } }),
            E("2", success: false, metadata: new { ErrorCode = "NotFound", AttemptedName = "a" }),
            E("3", success: false, metadata: new { ErrorCode = "AmbiguousMatch", AttemptedName = "c" }), E("4", success: false));
        Assert.Equal(4d, d.FailureAnalysis.Failures.Value);
        Assert.Equal(2, d.FailureAnalysis.Codes.Single(c => c.Code == "NotFound").Count);
        Assert.Equal("missing", d.FailureAnalysis.Codes.Single(c => c.Code == "NotFound").Reason);
        Assert.Equal("b", Assert.Single(d.FailureAnalysis.Codes.Single(c => c.Code == "NotFound").Suggestions));
        Assert.Equal(1, d.FailureAnalysis.Codes.Single(c => c.Code == "reason not recorded").Count);
        Assert.Equal(new FailedName("a", 2), d.FailureAnalysis.Names[0]);
        Assert.Equal(2, Assert.Single(d.FailureAnalysis.Repeated).Count);
    }

    [Fact]
    public void ContextSavings_ExactBaselineAndHistoricalExclusion()
    {
        var d = Details(E("1", metadata: new { WholeFileCharacters = 40000, ReturnedCharacters = 2000 }), E("2"));
        Assert.Equal(9500d, d.ContextSavings.EstimatedTokensAvoided.Value);
        Assert.Equal(1, d.ContextSavings.Baselines); Assert.Equal(1, d.ContextSavings.NoBaseline);
        Assert.Contains("not billing", d.ContextSavings.Baseline);
        Assert.Equal("NOT COLLECTED", Details(E("old")).ContextSavings.EstimatedTokensAvoided.State);
        Assert.Null(Assert.Single(Details(E("old")).ContextSavings.Operations).EstimatedTokensAvoided);
        Assert.Equal("ZERO", Details(E("full", metadata: new { WholeFileCharacters = 40, ReturnedCharacters = 40 })).ContextSavings.EstimatedTokensAvoided.State);
    }

    [Fact]
    public void EventHistoryCoverage_EmptyPreviousPeriodIsZeroOnlyWithKnownCoverage()
    {
        var unknown = AnalyticsReportAggregator.Compose(new(null, [E("1")]), Now, TimeSpan.FromDays(1));
        Assert.Equal("NOT COLLECTED", unknown.Reporting!.Availability["PreviousPeriod"].State);
        var known = AnalyticsReportAggregator.Compose(new(null, [E("1")], CollectionWindows:
            [new("live3", Now.AddDays(-3), Now)]), Now, TimeSpan.FromDays(1));
        Assert.Equal("ZERO", known.Reporting!.Availability["PreviousPeriod"].State);
        var empty = AnalyticsReportAggregator.Compose(new(null, []), Now, TimeSpan.FromDays(1));
        Assert.Equal("NOT COLLECTED", empty.Reporting!.Availability["Operations"].State);
        Assert.Null(empty.Reporting.Availability["Operations"].Value);
    }

    [Fact]
    public void UnsupportedSkipCounters_DistinguishZeroUnknownAndPositive()
    {
        Assert.Equal("NOT COLLECTED", Details(E("old", "index update")).UnsupportedFilesSkipped.State);
        Assert.Equal("ZERO", Details(E("new", "index update", metadata: new { UnsupportedFilesSkipped = 0 })).UnsupportedFilesSkipped.State);
        Assert.Equal(3d, Details(E("new", "index update", metadata: new { UnsupportedFilesSkipped = 3 })).UnsupportedFilesSkipped.Value);
    }

    [Fact]
    public void CorrelatedProtocolAndCliReads_CountBaselineOnce_ProtocolOnlyStillCounted()
    {
        var inner = E("inner", metadata: new { WholeFileCharacters = 40000, ReturnedCharacters = 2000, ReadEventId = "read-1", EventOrigin = "internal-read" });
        var outer = E("outer", metadata: new { WholeFileCharacters = 40000, ReturnedCharacters = 2000, ReadEventId = "read-1" });
        Assert.Equal(9500d, Details(inner, outer).ContextSavings.EstimatedTokensAvoided.Value);
        Assert.Equal(1, Assert.Single(Details(inner, outer).Sessions).Calls);
        Assert.Equal(9500d, Details(inner).ContextSavings.EstimatedTokensAvoided.Value);
    }

    [Fact]
    public void MonitoringOff_IsNotCollected_NotZeroCompliance()
    {
        var d = Details(E("1"));
        Assert.Equal("NOT COLLECTED", d.BypassVisibility.Candidates.State);
        Assert.Null(d.BypassVisibility.Candidates.Value);
        var report = AnalyticsReportAggregator.Compose(new(null, [E("1")]), Now, TimeSpan.FromDays(1));
        Assert.Equal("NOT COLLECTED", report.Reporting!.Availability["Timeouts"].State);
        foreach (var metric in d.Availability.Where(p => p.Key is "AuthenticatedIdentity" or "PolicyEnforcement" or "MergeOutcomes" or "SecretDetection"))
        { Assert.Equal("UNAVAILABLE", metric.Value.State); Assert.Null(metric.Value.Value); }
    }

    [Fact]
    public void Observations_CountClasses_ClipCompletedCoverage()
    {
        var d = Details(E("coverage", "fallback coverage", metadata: new { CoverageStart = Now.AddDays(-2), CoverageEnd = Now.AddMinutes(-10) }),
            E("a", "fallback observe", metadata: new { Classification = "bypass candidate" }),
            E("b", "fallback observe", metadata: new { Classification = "allowed" }),
            E("c", "fallback observe", metadata: new { Classification = "unclassified" }));
        Assert.Equal(1d, d.BypassVisibility.Candidates.Value); Assert.Equal(1d, d.BypassVisibility.Allowed.Value); Assert.Equal(1d, d.BypassVisibility.Unclassified.Value);
        Assert.Equal(Now.AddDays(-1), Assert.Single(d.BypassVisibility.Coverage).Start);
        Assert.Equal(Now.AddMinutes(-10), d.BypassVisibility.Coverage[0].End);
        var empty = Details(E("coverage", "fallback coverage", metadata: new { CoverageStart = Now.AddHours(-2), CoverageEnd = Now.AddHours(-1) }));
        Assert.Equal("ZERO", empty.BypassVisibility.Candidates.State);
    }

    [Theory]
    [InlineData("grep people src/db.py", "bypass candidate")]
    [InlineData("rg people src/db.py", "bypass candidate")]
    [InlineData("cat docs/plan.md", "bypass candidate")]
    [InlineData("sed -n '1,20p' src/db.py", "bypass candidate")]
    [InlineData("head -n 2 src/db.py", "bypass candidate")]
    [InlineData("tail src/db.py", "bypass candidate")]
    [InlineData("find src -name db.py", "bypass candidate")]
    [InlineData("cat logs/server.log", "allowed")]
    [InlineData("cat build/generated.py", "allowed")]
    [InlineData("cat unknown.txt", "allowed")]
    [InlineData("cat src/db.py | head", "unclassified")]
    [InlineData("rg people", "unclassified")]
    public void ShellClassification_IsConservative(string command, string classification)
    {
        var root = Path.GetTempPath();
        Assert.Equal(classification, ShellObservationCommand.Classify(root, command, ["src/db.py", "docs/plan.md", "build/generated.py"]).Classification);
    }

    [Fact]
    public void LatencyDailySlowestAndSessionTaskBreakdowns_AreExact()
    {
        var events = Enumerable.Range(1, 20).Select(i => E(i.ToString(), ms: i, task: "brief-054")).ToArray();
        var d = Details(events); var latency = Assert.Single(d.Latency);
        Assert.Equal(10, latency.P50Ms); Assert.Equal(19, latency.P95Ms); Assert.Equal(10.5, latency.AverageMs);
        Assert.Equal(20, Assert.Single(d.DailyActivity).Events); Assert.Equal(10, d.SlowestCalls.Count); Assert.Equal(20, d.SlowestCalls[0].DurationMs);
        Assert.Equal("brief-054", Assert.Single(d.Tasks).Task); Assert.Equal(20, Assert.Single(d.Sessions).Calls);
    }

    [Fact]
    public void JsonIsAdditive_HtmlPreservesSectionsAndEscapesFailureMetadata()
    {
        var report = AnalyticsReportAggregator.Compose(new(null, [E("1", success: false, metadata: new { ErrorCode = "<script>", FailureReason = "<img>" })]), Now, TimeSpan.FromDays(1));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(report));
        Assert.True(json.RootElement.TryGetProperty("ResponseCharacters", out _)); Assert.True(json.RootElement.TryGetProperty("OperationActivity", out _));
        Assert.True(json.RootElement.TryGetProperty("Reporting", out _));
        var html = AnalyticsHtmlRenderer.Render(report);
        foreach (var title in new[] { "Usage and validation reliability", "Repository activity", "Agent activity", "Operation activity", "Period comparison", "Operational efficiency", "Recommendations", "Data coverage and governance" })
            Assert.Contains(title, html);
        Assert.Contains("&lt;script&gt;", html); Assert.DoesNotContain("<script", html); Assert.DoesNotContain("<img", html);
        Assert.Contains("NOT COLLECTED", html); Assert.Contains("UNAVAILABLE", html);
    }

    [Fact]
    public async Task RealCli_ReadRecordsExactBaselineAndTask_ExportMergesWorkspaces_RefusesOverwrite()
    {
        await using var first = await Fixture.Create(); await using var second = await Fixture.Create();
        var read = await first.Cli("read", "file", "sample.py", "--range", "1:1", "--session", first.Session, "--agent", "deepseek", "--task", "brief-054", "--format", "json");
        Assert.Equal(0, read.ExitCode);
        using var readJson = JsonDocument.Parse(read.Output);
        var metadata = readJson.RootElement.GetProperty("Data").GetProperty("Metadata");
        Assert.Equal(40000, metadata.GetProperty("WholeFileCharacters").GetInt32()); Assert.Equal(2000, metadata.GetProperty("ReturnedCharacters").GetInt32());
        var a = Path.Combine(first.Root, "a.json"); var b = Path.Combine(second.Root, "b.json");
        Assert.Equal(0, (await first.Cli("report", "export", "--period", "1d", "--output", a)).ExitCode);
        Assert.Equal(0, (await second.Cli("report", "export", "--period", "1d", "--output", b)).ExitCode);
        var inputA = JsonSerializer.Deserialize<AnalyticsInput>(await File.ReadAllTextAsync(a))!;
        var inputB = JsonSerializer.Deserialize<AnalyticsInput>(await File.ReadAllTextAsync(b))!;
        Assert.Empty(inputA.Events.Select(e => e.Id).Intersect(inputB.Events.Select(e => e.Id)));
        Assert.Contains(inputA.Events, e => ReportingDetailsComposer.Text(e, "TaskLabel") == "brief-054");
        var output = Path.Combine(first.Root, "merged.json");
        Assert.Equal(0, (await first.Cli("report", "html", "--input", a, "--input", b, "--input", a, "--format", "json", "--output", output)).ExitCode);
        var report = JsonSerializer.Deserialize<AnalyticsReport>(await File.ReadAllTextAsync(output))!;
        Assert.Equal(inputA.Events.Count + inputB.Events.Count, report.Operations); Assert.Equal(2, report.Reporting!.Workspaces.Count);
        Assert.Equal(9500d, report.Reporting.ContextSavings.EstimatedTokensAvoided.Value);
        Assert.Equal(1, (await first.Cli("report", "export", "--output", a)).ExitCode);
        Assert.Equal(await File.ReadAllTextAsync(a), JsonSerializer.Serialize(inputA, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public async Task IndexHealth_DetectsMissingAndStale_UnknownHistoricalTimesRemainNull()
    {
        await using var f = await Fixture.Create();
        File.SetLastWriteTimeUtc(Path.Combine(f.Root, "sample.py"), DateTime.UtcNow.AddMinutes(1));
        var h = await IndexHealthCollector.CollectAsync(f.ConnectionString, f.Root); Assert.Equal(1, h.StaleFiles);
        await using (var c = new SqliteConnection(f.ConnectionString))
        { await c.OpenAsync(); await using var cmd = c.CreateCommand(); cmd.CommandText = "DELETE FROM file_index_observations"; await cmd.ExecuteNonQueryAsync(); }
        h = await IndexHealthCollector.CollectAsync(f.ConnectionString, f.Root); Assert.Null(h.StaleFiles); Assert.Equal(1, h.FilesWithoutIndexTime);
        File.Move(Path.Combine(f.Root, "sample.py"), Path.Combine(f.Root, "sample.saved"));
        h = await IndexHealthCollector.CollectAsync(f.ConnectionString, f.Root); Assert.Equal(1, h.MissingFiles); Assert.Equal("sample.py", Assert.Single(h.MissingPaths));
    }

    [Fact]
    public async Task CliObservation_RecordsClassificationButRejectsFutureCoverage_CleanlyRejectsNullInput()
    {
        await using var f = await Fixture.Create();
        var observation = await f.Cli("fallback", "observe", "--session", f.Session, "--command", "cat sample.py", "--agent", "deepseek", "--format", "json");
        Assert.Equal(0, observation.ExitCode);
        using var result = JsonDocument.Parse(observation.Output);
        Assert.Equal("bypass candidate", result.RootElement.GetProperty("Observation").GetProperty("Classification").GetString());
        var future = await f.Cli("fallback", "observe", "--session", f.Session, "--coverage-start", DateTimeOffset.UtcNow.ToString("O"), "--coverage-end", DateTimeOffset.UtcNow.AddDays(1).ToString("O"));
        Assert.Equal(1, future.ExitCode); Assert.Contains("future coverage", future.Output); Assert.DoesNotContain(" at ", future.Output);
        var invalid = Path.Combine(f.Root, "invalid.json");
        await File.WriteAllTextAsync(invalid, "{\"Events\":[null]}");
        var input = await f.Cli("report", "html", "--input", invalid, "--output", Path.Combine(f.Root, "invalid.html"));
        Assert.Equal(1, input.ExitCode); Assert.Contains("Each input event requires", input.Output); Assert.DoesNotContain(" at ", input.Output);
    }

    [Fact]
    public async Task FixtureDatabase_GeneratesSampleReport_WithLegacyFailuresSavingsAndObservedCoverage()
    {
        await using var f = await Fixture.Create();
        var store = new SqliteStorageInitializer(f.ConnectionString);
        foreach (var e in new[] { E("a", success: false, metadata: new { ErrorCode = "NotFound", AttemptedName = "missing", FailureReason = "not indexed", Suggestions = new[] { "sample" } }),
            E("b", success: false, metadata: new { ErrorCode = "NotFound", AttemptedName = "missing" }), E("legacy", success: false) })
            await store.AddOperationMetricAsync(new OperationMetric(e.Operation, DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(15), false, f.Session, e.MetadataJson, "codex"));
        await f.Cli("read", "file", "sample.py", "--range", "1:1", "--session", f.Session, "--task", "brief-054");
        await f.Cli("fallback", "observe", "--session", f.Session, "--agent", "codex", "--command", "cat sample.py",
            "--coverage-start", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"), "--coverage-end", DateTimeOffset.UtcNow.ToString("O"));
        await f.Cli("index", "update", "notes.txt", "--session", f.Session);
        var output = Path.Combine(f.Root, "sample.html");
        Assert.Equal(0, (await f.Cli("report", "html", "--output", output)).ExitCode);
        var html = await File.ReadAllTextAsync(output); Assert.Contains("9500", html); Assert.Contains("reason not recorded", html); Assert.Contains("bypass candidate", html.ToLowerInvariant());
        if (Environment.GetEnvironmentVariable("DEVX_SAMPLE_OUTPUT") is { } destination)
        {
            var eventsPath = Path.Combine(f.Root, "events.json");
            Assert.Equal(0, (await f.Cli("report", "export", "--output", eventsPath)).ExitCode);
            Directory.CreateDirectory(destination); File.Copy(output, Path.Combine(destination, "reporting-sample.html"), true);
            File.Copy(eventsPath, Path.Combine(destination, "reporting-events.json"), true);
            SqliteConnection.ClearAllPools(); File.Copy(f.Database, Path.Combine(destination, "reporting-fixture.db"), true);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; private set; } = Path.Combine(Path.GetTempPath(), "devx-report-test-" + Guid.NewGuid().ToString("N"));
        public string Database => Path.Combine(Root, ".devx", "devx.db");
        public string ConnectionString => new SqliteConnectionStringBuilder { DataSource = Database }.ToString();
        public string Session { get; private set; } = "";
        public static async Task<Fixture> Create()
        {
            var f = new Fixture(); Directory.CreateDirectory(Path.Combine(f.Root, ".devx"));
            await File.WriteAllTextAsync(Path.Combine(f.Root, "sample.py"), "#" + new string('x', 1999) + "\n#" + new string('y', 37998));
            await File.WriteAllTextAsync(Path.Combine(f.Root, "notes.txt"), "unsupported text");
            var store = new SqliteStorageInitializer(f.ConnectionString); await store.InitializeAsync();
            Assert.Equal(0, (await f.Cli("index", ".", "--agent", "codex")).ExitCode);
            var created = await f.Cli("session", "create", "--format", "json");
            Assert.Equal(0, created.ExitCode); using var json = JsonDocument.Parse(created.Output);
            f.Session = json.RootElement.GetProperty("Data").GetProperty("sessionId").GetString()!;
            var state = await store.GetSessionAsync(new GetSessionRequest(new SessionId(Guid.Parse(f.Session))));
            f.Root = (await store.GetRepositoryRootPathAsync(state!.RepositoryId))!;
            return f;
        }
        public async Task<(int ExitCode, string Output)> Cli(params string[] args)
        {
            var info = new ProcessStartInfo("dotnet") { WorkingDirectory = Root, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add(typeof(ArtifactCommand).Assembly.Location); foreach (var arg in args) info.ArgumentList.Add(arg);
            using var process = Process.Start(info)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(true); throw; }
            return (process.ExitCode, await stdout + await stderr);
        }
        public ValueTask DisposeAsync() { SqliteConnection.ClearAllPools(); Directory.Delete(Root, true); return ValueTask.CompletedTask; }
    }
}
