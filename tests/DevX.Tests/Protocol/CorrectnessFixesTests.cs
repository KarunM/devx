using System.Diagnostics;
using System.Text.Json;
using DevX.Domain;
using DevX.Infrastructure.Storage.SQLite;
using DevX.Indexer;
using DevX.Protocol;
using Microsoft.Data.Sqlite;
using Xunit;
using IOFile = System.IO.File;

namespace DevX.Tests.Protocol;

/// <summary>OSS21 C-1..C-3: absolute in-root patch paths, ripgrep argv/stdin, MCP test targets.</summary>
public sealed class CorrectnessFixesTests
{
    private sealed record Fixture(string Root, string Connection, SqliteStorageInitializer Store, ProtocolEngine Engine, SessionState Session);

    private static async Task<Fixture> CreateAsync(bool refreshAfterEdit = false)
    {
        var root = Directory.CreateTempSubdirectory("devx-oss21").FullName;
        var src = Path.Combine(root, "src");
        Directory.CreateDirectory(src);
        await IOFile.WriteAllTextAsync(Path.Combine(src, "Sample.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        await IOFile.WriteAllTextAsync(Path.Combine(src, "Alpha.cs"), "namespace Sample;\npublic class Alpha\n{\n    public void Before()\n    {\n    }\n}");
        var connection = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"devx-oss21-{Guid.NewGuid():N}.db") }.ToString();
        var store = new SqliteStorageInitializer(connection);
        await new RepositoryIndexingService(store).IndexAsync(root);
        await using var db = new SqliteConnection(connection);
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT id FROM repositories WHERE root_path = $root;";
        command.Parameters.AddWithValue("$root", Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)));
        var id = new RepositoryId(Guid.Parse((string)(await command.ExecuteScalarAsync())!));
        var session = await store.CreateSessionAsync(new CreateSessionRequest(id));
        return new Fixture(root, connection, store, new ProtocolEngine(new SearchService(store, store), store, store, new DotnetBuildTestService(), new PatchLifecycleService(), refreshAfterEdit), session);
    }

    private static ProtocolRequest PatchRequest(Fixture f, string operation, string path, string oldText, string newText, string? beforeState = null)
        => new("1.0", operation, f.Session.Id.Value.ToString(), JsonSerializer.SerializeToElement(new
        {
            version = "1.0",
            operations = new[] { new { operation = "ReplaceText", path, oldText, replacementText = newText, beforeState } }
        }));

    private static async Task<bool> HasSymbolAsync(Fixture f, string name)
    {
        var response = await f.Engine.ExecuteAsync(new ProtocolRequest("1.0", ProtocolOperations.SearchSymbol,
            f.Session.Id.Value.ToString(), JsonSerializer.SerializeToElement(new { query = name })));
        Assert.True(response.Success);
        return response.Data!.Value.GetProperty("Symbols").GetArrayLength() > 0;
    }

    [Fact]
    public async Task C1_AbsoluteInRootPatchPathStoresRelativeDirtyMarker()
    {
        var f = await CreateAsync();
        var absolute = Path.Combine(f.Root, "src", "Alpha.cs");
        var response = await f.Engine.ExecuteAsync(PatchRequest(f, ProtocolOperations.Patch, absolute, "Before", "After"));
        Assert.True(response.Success);

        var dirty = await f.Store.GetDirtyIndexRepositoryAsync(f.Root);
        Assert.NotNull(dirty);
        Assert.Equal(["src/Alpha.cs"], dirty!.DirtyFiles);

        await new RepositoryIndexingService(f.Store).IndexAsync(f.Root);
        Assert.False(await HasSymbolAsync(f, "Before"));
        Assert.True(await HasSymbolAsync(f, "After"));
    }

    [Fact]
    public async Task C1_AbsoluteInRootPatchWithAutoRefreshLeavesNoStaleMarker()
    {
        var f = await CreateAsync(refreshAfterEdit: true);
        var absolute = Path.Combine(f.Root, "src", "Alpha.cs");
        Assert.True((await f.Engine.ExecuteAsync(PatchRequest(f, ProtocolOperations.Patch, absolute, "Before", "After"))).Success);

        Assert.Null(await f.Store.GetDirtyIndexRepositoryAsync(f.Root));
        Assert.False(await HasSymbolAsync(f, "Before"));
        Assert.True(await HasSymbolAsync(f, "After"));
    }

    [Fact]
    public async Task C1_RelativePathDirtyRowUnchanged()
    {
        var f = await CreateAsync();
        var response = await f.Engine.ExecuteAsync(PatchRequest(f, ProtocolOperations.Patch, "src/Alpha.cs", "Before", "After"));
        Assert.True(response.Success);
        var dirty = await f.Store.GetDirtyIndexRepositoryAsync(f.Root);
        Assert.Equal(["src/Alpha.cs"], dirty!.DirtyFiles);
    }

    [Fact]
    public async Task C1_RollbackUsesSameNormalization()
    {
        var f = await CreateAsync();
        var absolute = Path.Combine(f.Root, "src", "Alpha.cs");
        var original = await IOFile.ReadAllTextAsync(absolute);
        var patch = PatchRequest(f, ProtocolOperations.Patch, absolute, "Before", "After");
        Assert.True((await f.Engine.ExecuteAsync(patch)).Success);
        await f.Store.ClearDirtyIndexFilesAsync(f.Root, ["src/Alpha.cs"]);
        Assert.Null(await f.Store.GetDirtyIndexRepositoryAsync(f.Root));

        var rollback = await f.Engine.ExecuteAsync(PatchRequest(f, ProtocolOperations.PatchRollback, absolute, "Before", "After", original));
        Assert.True(rollback.Success);
        var dirty = await f.Store.GetDirtyIndexRepositoryAsync(f.Root);
        Assert.NotNull(dirty);
        Assert.Equal(["src/Alpha.cs"], dirty!.DirtyFiles);
    }

    [Fact]
    public async Task C1_StoreMapsAbsoluteInRootButKeepsOutsideRootPathsAsGiven()
    {
        var f = await CreateAsync();
        var outside = Path.Combine(Path.GetTempPath(), "elsewhere", "x.cs");
        Assert.True(await f.Store.TryMarkIndexFilesDirtyAsync(f.Root, [Path.Combine(f.Root, "src", "Alpha.cs"), "src/Alpha.cs", outside]));
        var dirty = await f.Store.GetDirtyIndexRepositoryAsync(f.Root);
        Assert.Equal(["src/Alpha.cs", outside.TrimStart('/')], dirty!.DirtyFiles.OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    private static string FakeRg(string body)
    {
        var dir = Directory.CreateTempSubdirectory("devx-oss21-rg").FullName;
        var exe = Path.Combine(dir, "rg");
        IOFile.WriteAllText(exe, "#!/bin/sh\n" + body);
        if (!OperatingSystem.IsWindows()) // chmod-based fake rg; no Windows equivalent
            IOFile.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return exe;
    }

    [Fact]
    public void C2_RipgrepArgvHasExplicitPathAfterPattern()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("devx-oss21-argv").FullName;
        var argvFile = Path.Combine(root, "argv.txt");
        var exe = FakeRg($"for a in \"$@\"; do printf '%s\\n' \"$a\" >> '{argvFile}'; done\nprintf 'a.txt:1:hit\\n'\n");

        Assert.True(RipgrepContentSearcher.TrySearch(root, "needle", exe, out _, out _, TimeSpan.FromSeconds(10)));

        var argv = IOFile.ReadAllLines(argvFile);
        Assert.Equal(["--", "needle", "."], argv[^3..]);
    }

    [Fact]
    public void C2_RipgrepWithPipedStdinReturnsMatches()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("devx-oss21-stdin").FullName;
        var exe = FakeRg("cat >/dev/null\nprintf 'a.txt:1:hit\\n'\n");
        var timer = Stopwatch.StartNew();

        var answered = RipgrepContentSearcher.TrySearch(root, "hit", exe, out var hits, out _, TimeSpan.FromSeconds(10));

        Assert.True(answered);
        Assert.Equal(("a.txt", 1, "hit"), (hits.Single().Path, hits.Single().Line, hits.Single().Text));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(8), $"blocked on stdin for {timer.Elapsed}");
    }

    [Fact]
    public void C2_DotSlashHitPathsAreStrippedToIndexedForm()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("devx-oss21-dot").FullName;
        var exe = FakeRg("printf './src/a.txt:3:hit\\n./b.txt:1:hit\\n'\n");

        Assert.True(RipgrepContentSearcher.TrySearch(root, "hit", exe, out var hits, out _, TimeSpan.FromSeconds(10)));

        Assert.Equal(["src/a.txt", "b.txt"], hits.Select(h => h.Path).ToArray());
    }

    private static string? RealRipgrep()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, "rg");
            if (IOFile.Exists(candidate)) return candidate;
        }
        return null;
    }

    [Fact]
    public void C2_RealRipgrepHitPathsMatchIndexedPaths()
    {
        var rg = RealRipgrep();
        if (rg is null || OperatingSystem.IsWindows()) return; // skipped: no rg on PATH
        var root = Directory.CreateTempSubdirectory("devx-oss21-real").FullName;
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        IOFile.WriteAllText(Path.Combine(root, "docs", "n.md"), "UniqueRealNeedle here\n");

        Assert.True(RipgrepContentSearcher.TrySearch(root, "UniqueRealNeedle", rg, out var hits, out _, TimeSpan.FromSeconds(20)));

        var hit = Assert.Single(hits);
        Assert.Equal("docs/n.md", hit.Path);
        Assert.Equal(1, hit.Line);
    }

    private sealed class CapturingEngine : IProtocolEngine
    {
        public ProtocolRequest? Last;
        public Task<ProtocolResponse> ExecuteAsync(ProtocolRequest request, CancellationToken cancellationToken = default)
        {
            Last = request;
            return Task.FromResult(new ProtocolResponse(true, JsonSerializer.SerializeToElement(new { }), [], []));
        }
    }

    [Fact]
    public async Task C3_McpTestPayloadForwardsTestFilesAsTargets()
    {
        var f = await CreateAsync();
        var engine = new CapturingEngine();
        var server = new McpReferenceServer(engine, f.Store, f.Store, f.Session.Id.Value.ToString());

        await server.CallToolAsync("test", JsonSerializer.SerializeToElement(new { sessionId = f.Session.Id.Value.ToString(), testFiles = new[] { "tests/test_target.py" } }));

        Assert.Equal(ProtocolOperations.RunTests, engine.Last!.Operation);
        var payload = engine.Last.Payload;
        Assert.Equal(["tests/test_target.py"], payload.GetProperty("targets").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.Equal(["tests/test_target.py"], payload.GetProperty("testFiles").EnumerateArray().Select(x => x.GetString()!).ToArray());

        await server.CallToolAsync("test", JsonSerializer.SerializeToElement(new { sessionId = f.Session.Id.Value.ToString() }));
        Assert.False(engine.Last.Payload.TryGetProperty("targets", out _));
    }
}
