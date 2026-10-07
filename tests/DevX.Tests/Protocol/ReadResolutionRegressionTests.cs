using System.Text.Json;
using DevX.Application;
using DevX.Domain;
using DevX.Indexer;
using DevX.Infrastructure.Storage.SQLite;
using DevX.Protocol;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevX.Tests.Protocol;

public sealed class ReadResolutionRegressionTests
{
    private sealed record Fixture(string Root, SqliteStorageInitializer Store, ProtocolEngine Engine, SessionState Session)
    {
        public Task<ProtocolResponse> Read(string name) => Engine.ExecuteAsync(new("1.0", "READ_METHOD", Session.Id.Value.ToString(), JsonSerializer.SerializeToElement(new { query = name })));
    }

    private static async Task<Fixture> Create()
    {
        var parent = Directory.CreateTempSubdirectory("devx-read-regression-").FullName;
        var root = Path.Combine(parent, "scratchpad", "project-a");
        Directory.CreateDirectory(root);
        await System.IO.File.WriteAllTextAsync(Path.Combine(root, "auth.py"), "def revoke_session():\n    return 'auth'\n");
        await System.IO.File.WriteAllTextAsync(Path.Combine(root, "db.py"), "def revoke_session():\n    return 'db'\n");
        await System.IO.File.WriteAllTextAsync(Path.Combine(root, "life.py"), "def today_line():\n    return 'today'\n");
        await System.IO.File.WriteAllTextAsync(Path.Combine(root, "guards.py"), "MEMORY_CANON_RULE = (\n    'first',\n    'second',\n)\n");
        Directory.CreateDirectory(Path.Combine(root, "tests"));
        await System.IO.File.WriteAllTextAsync(Path.Combine(root, "tests", "filing_model_eval.py"), "def main():\n    return 1\n\nclass Fake:\n    def today_line(self):\n        return 'fake'\n");
        await System.IO.File.WriteAllTextAsync(Path.Combine(root, "notes.md"), "# Planning\nSection contents.\n");
        Directory.CreateDirectory(Path.Combine(root, ".devx"));
        var store = new SqliteStorageInitializer(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, ".devx", "devx.db") }.ToString());
        await new RepositoryIndexingService(store).IndexAsync(root);
        var repo = await store.GetRepositoryByRootPathAsync(root);
        var session = await store.CreateSessionAsync(new CreateSessionRequest(repo!.Id));
        return new(root, store, new ProtocolEngine(new SearchService(store, store), store, store), session);
    }

    [Fact]
    public async Task FieldRead_ReturnsFullMultilineAssignment()
    {
        var f = await Create();
        var response = await f.Read("guards.<module>.MEMORY_CANON_RULE");
        Assert.True(response.Success);
        var data = response.Data!.Value.Deserialize<SourceReadResponse>()!;
        Assert.Equal("MEMORY_CANON_RULE = (\n    'first',\n    'second',\n)", data.Source);
        Assert.Equal(1, data.StartLine); Assert.Equal(4, data.EndLine);
        Assert.Equal("Field", response.Data.Value.GetProperty("Metadata").GetProperty("SymbolKind").GetString());
    }

    [Fact]
    public async Task ExistingMethodRead_PreservesSourceIdentityLineCountsAndTruncation()
    {
        var f = await Create();
        var previous = Assert.Single(await f.Store.FindMethodSourcesAsync("auth.<module>.revoke_session"));
        var response = await f.Read("auth.<module>.revoke_session");
        Assert.True(response.Success);
        var data = response.Data!.Value.Deserialize<SourceReadResponse>()!;
        Assert.Equal(previous.Id, data.SymbolId); Assert.Equal(previous.Source, data.Source);
        Assert.Equal(2, data.TotalLines); Assert.Equal(2, data.ReturnedLines); Assert.False(data.Truncated);
    }

    [Theory]
    [InlineData("auth.revoke_session", "auth.py", "module-path")]
    [InlineData("db.revoke_session", "db.py", "module-path")]
    [InlineData("scratchpad.project-a.auth.revoke_session", "auth.py", "prefix-stripped")]
    [InlineData("project-a.life.today_line", "life.py", "prefix-stripped")]
    [InlineData("tests.filing_model_eval.main", "tests/filing_model_eval.py", "module-path")]
    [InlineData("tests.filing_model_eval.Fake.today_line", "tests/filing_model_eval.py", "exact")]
    public async Task ModuleRead_UsesFileConstraint(string query, string path, string via)
    {
        var f = await Create(); var response = await f.Read(query);
        Assert.True(response.Success, string.Join(";", response.Errors.Select(e => e.Message)));
        Assert.Equal(path, response.Data!.Value.GetProperty("Path").GetString());
        Assert.Equal(via, response.Data.Value.GetProperty("Metadata").GetProperty("ResolvedVia").GetString());
    }

    [Fact]
    public async Task AmbiguousShortRead_ListsEveryCandidateWithPathLinesAndKind()
    {
        var f = await Create(); var response = await f.Read("revoke_session");
        var error = Assert.Single(response.Errors); Assert.Equal("AmbiguousMatch", error.Code);
        Assert.Equal(2, error.Candidates!.Count);
        Assert.Equal(new[] { "auth.py", "db.py" }, error.Candidates.Select(c => c.FilePath).Order().ToArray());
        Assert.All(error.Candidates, c => { Assert.Equal(1, c.StartLine); Assert.Equal(2, c.EndLine); Assert.Equal("Method", c.Kind); });
    }

    [Fact]
    public async Task NotFound_DoesNotSuggestIdenticalNameOrSelectWrongModule()
    {
        var f = await Create(); var response = await f.Read("other-checkout.auth.revoke_session");
        var error = Assert.Single(response.Errors); Assert.Equal("NotFound", error.Code);
        Assert.Contains("not in this workspace:", error.Message);
        Assert.DoesNotContain("other-checkout.auth.revoke_session", error.Suggestions!);
    }

    [Fact]
    public async Task MarkdownMethodRead_ReturnsSectionAfterPrefixStrip()
    {
        var f = await Create();
        var symbols = await f.Store.FindSymbolsByNameAsync("Planning");
        var symbol = Assert.Single(symbols);
        var response = await f.Read("scratchpad.project-a." + symbol.QualifiedName);
        Assert.True(response.Success);
        Assert.Equal("section", response.Data!.Value.GetProperty("Kind").GetString());
        Assert.Contains("Section contents.", response.Data.Value.GetProperty("Source").GetString());
    }

    [Fact]
    public async Task MultilineIndexUpdate_ContinuesPastMissingFile()
    {
        var f = await Create();
        var result = await new RepositoryIndexingService(f.Store).ReindexFilesAsync(f.Root, ["auth.py\ndb.py\nmissing.py"]);
        Assert.Equal(2, result.UpdatedFiles); Assert.Equal(1, result.SkippedFiles);
        Assert.Equal("missing.py", Assert.Single(result.SkippedFilePaths).Path);
        Assert.Equal("ERROR: file missing", result.SkippedFilePaths[0].Reason);
    }

    [Fact]
    public async Task TextIndexUpdate_ReturnsWarningWithoutError()
    {
        var f = await Create(); await System.IO.File.WriteAllTextAsync(Path.Combine(f.Root, "notes.txt"), "hello");
        var result = await new RepositoryIndexingService(f.Store).ReindexFilesAsync(f.Root, ["notes.txt"]);
        Assert.Equal(0, result.UpdatedFiles); Assert.Equal(1, result.SkippedFiles);
        Assert.DoesNotContain("ERROR:", Assert.Single(result.SkippedFilePaths).Reason);
    }

    [Fact]
    public async Task DeletedIndexedFile_IsReportedByDoctorAndBlockedByReadAndFlaggedBySearch()
    {
        var f = await Create(); System.IO.File.Delete(Path.Combine(f.Root, "auth.py"));
        var service = new RuntimeHealthService();
        var health = await service.EvaluateAsync(f.Root, true, 7, indexedPaths: await f.Store.GetIndexedFilePathsAsync(f.Root));
        Assert.Contains(service.BuildDoctor(health).Findings, finding => finding.Summary == "1 indexed files missing on disk: auth.py");
        var read = await f.Read("auth.revoke_session");
        Assert.Equal("FileMissing", Assert.Single(read.Errors).Code);
        var search = await new SearchService(f.Store, f.Store).SearchSymbolAsync(new("revoke_session"));
        Assert.Equal("file missing", search.Symbols.Single(s => s.FilePath == "auth.py").FileStatus);
        var files = await f.Store.SearchFilesAsync("auth.py");
        Assert.Equal("file missing", Assert.Single(files).FileStatus);
        var doctor = await Cli(f, "doctor", "--session", f.Session.Id.Value.ToString(), "--format", "json");
        Assert.Contains("1 indexed files missing on disk: auth.py", doctor.Output);
        Assert.Equal("", doctor.Error);
        var cliHealth = await Cli(f, "health", "--session", f.Session.Id.Value.ToString(), "--format", "json");
        Assert.Contains("1 indexed files missing on disk: auth.py", cliHealth.Output);
    }

    private static async Task<(int ExitCode, string Output, string Error)> Cli(Fixture fixture, params string[] args)
    {
        var info = new System.Diagnostics.ProcessStartInfo("dotnet") { WorkingDirectory = fixture.Root, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(typeof(DevX.Cli.ArtifactCommand).Assembly.Location);
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await output, await error);
    }

    [Fact]
    public async Task CliMultilineUpdate_ReturnsZeroForPartialSuccess_OneWhenAllMissing_NoStackTrace()
    {
        var f = await Create();
        var result = await Cli(f, "index", "update", "auth.py\ndb.py\nmissing.py", "--session", f.Session.Id.Value.ToString(), "--format", "json");
        Assert.Equal(0, result.ExitCode); Assert.Equal("", result.Error);
        using var doc = JsonDocument.Parse(result.Output);
        Assert.Equal(2, doc.RootElement.GetProperty("UpdatedFiles").GetInt32());
        Assert.Equal("missing.py", doc.RootElement.GetProperty("SkippedFilePaths")[0].GetProperty("Path").GetString());
        var failed = await Cli(f, "index", "update", "missing.py", "--session", f.Session.Id.Value.ToString());
        Assert.Equal(1, failed.ExitCode); Assert.Equal("", failed.Error);
        Assert.Contains("ERROR: file missing", failed.Output); Assert.DoesNotContain("Exception", failed.Output);
    }

    [Fact]
    public async Task CliTextUpdate_ReturnsZeroAndWarning()
    {
        var f = await Create(); await System.IO.File.WriteAllTextAsync(Path.Combine(f.Root, "notes.txt"), "text");
        var result = await Cli(f, "index", "update", "notes.txt", "--session", f.Session.Id.Value.ToString());
        Assert.Equal(0, result.ExitCode); Assert.Equal("", result.Error);
        Assert.Contains("SKIPPED: notes.txt", result.Output);
    }
}
