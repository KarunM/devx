using DevX.Domain;
using DevX.Infrastructure.Storage.SQLite;
using DevX.Indexer;
using DevX.Protocol;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace DevX.Tests.Search;

public sealed class ContentSearchCoverageTests
{
    private static async Task<string> CreateRootAsync(string relativeFile, string body, string rgScript)
    {
        var root = Directory.CreateTempSubdirectory("devx-search-coverage").FullName;
        var filePath = Path.Combine(root, relativeFile);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await System.IO.File.WriteAllTextAsync(filePath, body);
        var rgPath = Path.Combine(root, "tools", "rg");
        Directory.CreateDirectory(Path.GetDirectoryName(rgPath)!);
        await System.IO.File.WriteAllTextAsync(rgPath, rgScript);
        if (!OperatingSystem.IsWindows())
            System.IO.File.SetUnixFileMode(rgPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.CreateDirectory(Path.Combine(root, ".devx"));
        await System.IO.File.WriteAllTextAsync(Path.Combine(root, ".devx", "config.json"), "{ \"rg\": { \"path\": \"tools/rg\" } }\n");
        return root;
    }

    private static async Task<(SqliteStorageInitializer Store, string Connection)> CreateStoreAsync()
    {
        var connection = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"devx-search-coverage-{Guid.NewGuid():N}.db") }.ToString();
        var store = new SqliteStorageInitializer(connection);
        await store.InitializeAsync();
        return (store, connection);
    }

    [Fact]
    public async Task FallbackReportsAnObservedExtraMatchAndExactLimitAsComplete()
    {
        if (OperatingSystem.IsWindows()) return;
        var content = string.Join('\n', Enumerable.Repeat("needle", 101));
        var root = await CreateRootAsync("notes.md", content, "#!/bin/sh\nexit 2\n");
        var (store, _) = await CreateStoreAsync();
        await new RepositoryIndexingService(store).IndexAsync(root);

        var truncated = await store.SearchFileContentsWithCoverageAsync("needle", 100);
        var exact = await store.SearchFileContentsWithCoverageAsync("needle", 101);

        Assert.Equal(100, truncated.Matches.Count);
        Assert.True(truncated.Coverage.OutputTruncated);
        Assert.False(truncated.Coverage.Complete);
        Assert.True(truncated.Coverage.FallbackUsed);
        Assert.Equal(101, exact.Matches.Count);
        Assert.False(exact.Coverage.OutputTruncated);
        Assert.True(exact.Coverage.Complete);

        var noMatch = await store.SearchFileContentsWithCoverageAsync("absent-token");
        Assert.Empty(noMatch.Matches);
        Assert.False(noMatch.Coverage.OutputTruncated);
        Assert.True(noMatch.Coverage.Complete);

        var noOp = await store.SearchFileContentsWithCoverageAsync("");
        Assert.Null(noOp.Coverage.OutputTruncated);
        Assert.Null(noOp.Coverage.Complete);
        Assert.Contains(noOp.Coverage.Warnings, warning => warning.Contains("No scan was performed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingIndexedFileMakesFallbackCoverageUnknown()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = await CreateRootAsync("notes.md", "needle\n", "#!/bin/sh\nexit 2\n");
        var (store, _) = await CreateStoreAsync();
        await new RepositoryIndexingService(store).IndexAsync(root);
        System.IO.File.Delete(Path.Combine(root, "notes.md"));

        var result = await store.SearchFileContentsWithCoverageAsync("needle");

        Assert.Empty(result.Matches);
        Assert.Null(result.Coverage.Complete);
        Assert.Contains(result.Coverage.Warnings, warning => warning.Contains("could not be searched completely", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GlobalSearchMapsDuplicateRelativePathsToTheirOwnRoots()
    {
        if (OperatingSystem.IsWindows()) return;
        var (store, _) = await CreateStoreAsync();
        var firstRoot = await CreateRootAsync("src/notes.md", "needle first\n", "#!/bin/sh\nexit 2\n");
        var secondRoot = await CreateRootAsync("src/notes.md", "needle second\n", "#!/bin/sh\nexit 2\n");
        var indexer = new RepositoryIndexingService(store);
        await indexer.IndexAsync(firstRoot);
        await indexer.IndexAsync(secondRoot);

        var result = await store.SearchFileContentsWithCoverageAsync("needle");

        Assert.Equal(2, result.Matches.Count);
        Assert.All(result.Matches, match => Assert.Equal("src/notes.md", match.Path));
        Assert.Equal(2, result.Matches.Select(match => match.FileId).Distinct(StringComparer.Ordinal).Count());
        Assert.True(result.Coverage.Complete);
    }

    [Fact]
    public async Task RipgrepDoesNotClaimCompleteCoverageForIgnoredIndexedFiles()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = await CreateRootAsync("secret.md", "needle\n", "#!/bin/sh\nexit 0\n");
        var (store, _) = await CreateStoreAsync();
        await new RepositoryIndexingService(store).IndexAsync(root);
        await System.IO.File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "secret.md\n");
        Assert.Contains(await store.SearchFilesAsync("secret"), file => file.Path == "secret.md");

        var result = await store.SearchFileContentsWithCoverageAsync("needle");

        Assert.Empty(result.Matches);
        Assert.Null(result.Coverage.Complete);
        Assert.Contains(result.Coverage.Warnings, warning => warning.Contains("ignore and hidden-file rules", StringComparison.Ordinal));
    }

    [Fact]
    public async Task McpSearchJsonAndAgentTextExposeCoverageAndWarnings()
    {
        if (OperatingSystem.IsWindows()) return;
        var body = string.Join('\n', Enumerable.Repeat("needle", 101));
        var rgScript = "#!/bin/sh\ni=1\nwhile [ $i -le 101 ]; do printf 'notes.md:%s:needle\\n' $i; i=$((i+1)); done\n";
        var root = await CreateRootAsync("notes.md", body, rgScript);
        var (store, connectionString) = await CreateStoreAsync();
        await new RepositoryIndexingService(store).IndexAsync(root);
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM repositories WHERE root_path = $root;";
        command.Parameters.AddWithValue("$root", root);
        var repositoryId = Guid.Parse((string)(await command.ExecuteScalarAsync())!);
        var session = await store.CreateSessionAsync(new CreateSessionRequest(new RepositoryId(repositoryId)));
        var server = new McpReferenceServer(new ProtocolEngine(new SearchService(store, store), store, store), store, store, session.Id.Value.ToString());
        var arguments = new { sessionId = session.Id.Value.ToString(), kind = "text", query = "needle" };

        var json = await server.CallToolAsync("search", JsonSerializer.SerializeToElement(new { arguments.sessionId, arguments.kind, arguments.query, format = "json" }));
        var coverage = FindProperty(json.Content, "contentCoverage");
        Assert.False(FindProperty(coverage, "complete").GetBoolean());
        Assert.True(FindProperty(coverage, "outputTruncated").GetBoolean());
        Assert.Equal("ripgrep", FindProperty(coverage, "engine").GetString());

        var agent = await server.CallToolAsync("search", JsonSerializer.SerializeToElement(new { arguments.sessionId, arguments.kind, arguments.query, format = "agent" }));
        Assert.Contains("More matches exist", agent.Content.GetProperty("text").GetString());
        Assert.Contains("Search coverage note", agent.Content.GetProperty("text").GetString());
        Assert.Contains("ignore and hidden-file rules", agent.Content.GetProperty("text").GetString());
    }

    [Fact]
    public async Task LegacyStoreKeepsItsApiAndReportsUnknownCoverage()
    {
        var legacyStore = DispatchProxy.Create<IRepositoryStore, LegacyRepositoryStoreProxy>();
        var search = new SearchService(legacyStore, new SqliteStorageInitializer(new SqliteConnectionStringBuilder { DataSource = ":memory:" }.ToString()));

        var result = await search.SearchTextAsync(new SearchTextRequest("needle"));

        Assert.Empty(result.ContentMatches!);
        Assert.NotNull(result.ContentCoverage);
        Assert.Null(result.ContentCoverage!.Complete);
        Assert.Null(result.ContentCoverage.OutputTruncated);
        Assert.Equal("unknown", result.ContentCoverage.Engine);
    }

    [Fact]
    public void RipgrepScanCeilingIsReported()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("devx-rg-cap").FullName;
        var executable = Path.Combine(root, "rg");
        System.IO.File.WriteAllText(executable, "#!/bin/sh\ni=1\nwhile [ $i -le 5001 ]; do printf 'notes.md:%s:needle\\n' $i; i=$((i+1)); done\n");
        System.IO.File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var answered = RipgrepContentSearcher.TrySearch(root, "needle", executable, out var hits, out var scanCapped, TimeSpan.FromSeconds(5));

        Assert.True(answered);
        Assert.True(scanCapped);
        Assert.Equal(5000, hits.Count);
    }

    [Fact]
    public void RipgrepSilentProcessHasABoundedTimeout()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("devx-rg-timeout").FullName;
        var executable = Path.Combine(root, "rg");
        System.IO.File.WriteAllText(executable, "#!/bin/sh\nsleep 10\n");
        System.IO.File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var timer = Stopwatch.StartNew();

        var answered = RipgrepContentSearcher.TrySearch(root, "needle", executable, out _, out var scanCapped, TimeSpan.FromMilliseconds(150));

        Assert.False(answered);
        Assert.False(scanCapped);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3), $"process timeout took {timer.Elapsed}");
    }

    private static JsonElement FindProperty(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
                if (ContainsProperty(property.Value, name)) return FindProperty(property.Value, name);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                if (ContainsProperty(item, name)) return FindProperty(item, name);
        }
        throw new KeyNotFoundException($"JSON property '{name}' was not found.");
    }

    private static bool ContainsProperty(JsonElement element, string name)
        => element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().Any(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || ContainsProperty(property.Value, name)),
            JsonValueKind.Array => element.EnumerateArray().Any(item => ContainsProperty(item, name)),
            _ => false
        };
}

public class LegacyRepositoryStoreProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => targetMethod?.Name switch
        {
            nameof(IRepositoryStore.SearchFileContentsAsync) => Task.FromResult<IReadOnlyList<ContentSearchResult>>([]),
            nameof(IRepositoryStore.SearchFilesAsync) => Task.FromResult<IReadOnlyList<FileSearchResult>>([]),
            _ => throw new NotSupportedException($"Unexpected legacy-store call: {targetMethod?.Name}")
        };
}
