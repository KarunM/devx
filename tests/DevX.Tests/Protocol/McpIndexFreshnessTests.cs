using System.Text.Json;
using DevX.Domain;
using DevX.Infrastructure.Storage.SQLite;
using DevX.Indexer;
using DevX.Protocol;
using Microsoft.Data.Sqlite;
using Xunit;
using IOFile = System.IO.File;

namespace DevX.Tests.Protocol;

/// <summary>
/// Item 3 of OSS10. Call 2 of the LG1 transcript was a full `index` of 3,292 files against an
/// index that was already built: the server knew the index was current and let the agent ask
/// anyway, which is the most expensive question in the tool set.
/// </summary>
public sealed class McpIndexFreshnessTests
{
    [Fact]
    public async Task WholeRepositoryIndex_WhenNothingChanged_SaysAlreadyCurrent()
    {
        var f = await Fixture.CreateAsync(changedFiles: []);

        var result = await f.Server.CallToolAsync("index", JsonSerializer.SerializeToElement(new { sessionId = f.SessionId }));

        Assert.False(result.IsError);
        Assert.Contains("already current (", TextOf(result));
        Assert.Equal(0, f.FullIndexCalls);
    }

    [Fact]
    public async Task WholeRepositoryIndex_WhenFilesChanged_Reindexes()
    {
        var f = await Fixture.CreateAsync(changedFiles: ["src/Sample/Program.cs"]);

        var result = await f.Server.CallToolAsync("index", JsonSerializer.SerializeToElement(new { sessionId = f.SessionId }));

        Assert.False(result.IsError);
        Assert.DoesNotContain("already current", TextOf(result));
        Assert.Equal(1, f.FullIndexCalls);
    }

    [Fact]
    public async Task WholeRepositoryIndex_RemovesMissingUntrackedIndexedRowsAndReportsCount()
    {
        var f = await Fixture.CreateAsync(changedFiles: [], includeExtraFile: true);
        System.IO.File.Delete(Path.Combine(f.Root, "src", "Sample", "Program.cs"));

        var result = await f.Server.CallToolAsync("index", JsonSerializer.SerializeToElement(new { sessionId = f.SessionId }));

        Assert.False(result.IsError);
        Assert.Contains("re-indexed 0, removed 1", TextOf(result));
        var indexedPaths = await f.Store.GetIndexedFilePathsAsync(f.Root);
        Assert.DoesNotContain("src/Sample/Program.cs", indexedPaths);
        Assert.Contains("src/Sample/Extra.cs", indexedPaths);
    }

    [Fact]
    public async Task Initialize_RemovesMissingIndexedRowsBeforeRefreshingGitChanges()
    {
        var f = await Fixture.CreateAsync(changedFiles: []);
        System.IO.File.Delete(Path.Combine(f.Root, "src", "Sample", "Program.cs"));

        await f.Server.InitializeAsync();

        Assert.DoesNotContain("src/Sample/Program.cs", await f.Store.GetIndexedFilePathsAsync(f.Root));
        Assert.Equal(new[] { "src/Sample/Program.cs" }, Assert.Single(f.ReindexRequests));
    }

    [Fact]
    public async Task Initialize_RefreshesTheChangedFilesOnly()
    {
        var f = await Fixture.CreateAsync(changedFiles: ["src/Sample/Program.cs", "src/Sample/Extra.cs"]);

        await f.Server.InitializeAsync();

        Assert.Empty(f.FullReindexRequests);
        var request = Assert.Single(f.ReindexRequests);
        Assert.Equal(new[] { "src/Sample/Extra.cs", "src/Sample/Program.cs" }, request.OrderBy(path => path, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Initialize_WhenNothingChanged_DoesNotIndex()
    {
        var f = await Fixture.CreateAsync(changedFiles: []);

        await f.Server.InitializeAsync();

        Assert.Empty(f.ReindexRequests);
    }

    [Fact]
    public async Task Initialize_WhenChangedFilesCannotBeRead_StillReturnsTools()
    {
        // A plain directory with no git must not stop the server from starting.
        var f = await Fixture.CreateAsync(changedFiles: null);

        var initialized = await f.Server.InitializeAsync();

        Assert.NotEmpty(initialized.Tools);
        Assert.Empty(f.ReindexRequests);
    }

    [Fact]
    public async Task IndexToolDescription_SaysItIsRarelyNeeded()
    {
        var f = await Fixture.CreateAsync(changedFiles: []);

        var tools = await f.Server.ListToolsAsync();
        var index = tools.Where(tool => tool.Name == "index").ToList();

        var descriptor = Assert.Single(index);
        Assert.Contains("Rarely needed", descriptor.Description);
    }

    private static string TextOf(McpToolCallResult result)
        => result.Content.ValueKind == JsonValueKind.Object && result.Content.TryGetProperty("text", out var text)
            ? text.GetString() ?? string.Empty
            : result.Content.GetRawText();

    private sealed class Fixture
    {
        public required McpReferenceServer Server { get; init; }
        public required SessionState Session { get; init; }
        public required string Root { get; init; }
        public required SqliteStorageInitializer Store { get; init; }
        public required List<string[]> ReindexRequests { get; init; }
        public required List<string[]> FullReindexRequests { get; init; }

        public string SessionId => Session.Id.Value.ToString();

        /// <summary>Shared by reference, so it reflects calls made after the fixture was built.</summary>
        public int FullIndexCalls => FullReindexRequests.Count;

        /// <summary>`changedFiles: null` means the repository cannot answer the question.</summary>
        public static async Task<Fixture> CreateAsync(IReadOnlyList<string>? changedFiles, bool includeExtraFile = false)
        {
            var root = Directory.CreateTempSubdirectory("devx-mcp-freshness").FullName;
            var projectDirectory = Path.Combine(root, "src", "Sample");
            Directory.CreateDirectory(projectDirectory);
            await IOFile.WriteAllTextAsync(Path.Combine(projectDirectory, "Sample.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);
            await IOFile.WriteAllTextAsync(Path.Combine(projectDirectory, "Program.cs"), """
                namespace Sample;
                public static class Program
                {
                    public static void Run() { }
                }
                """);

            if (includeExtraFile)
            {
                await IOFile.WriteAllTextAsync(Path.Combine(projectDirectory, "Extra.cs"), "namespace Sample; public sealed class Extra { }\n");
            }

            var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"devx-mcp-fresh-{Guid.NewGuid():N}.db") }.ToString();
            var store = new SqliteStorageInitializer(connectionString);
            await store.InitializeAsync();
            var indexer = new RepositoryIndexingService(store);
            await indexer.IndexAsync(root);

            var repositoryId = await GetRepositoryIdAsync(connectionString);
            var session = await store.CreateSessionAsync(new CreateSessionRequest(new RepositoryId(Guid.Parse(repositoryId))));
            var engine = new ProtocolEngine(new SearchService(store, store), store, store);

            var reindexRequests = new List<string[]>();
            var fullReindexRequests = new List<string[]>();

            var server = new McpReferenceServer(
                engine,
                store,
                store,
                session.Id.Value.ToString(),
                (files, cancellationToken) =>
                {
                    reindexRequests.Add(files);
                    return indexer.ReindexFilesAsync(root, files, cancellationToken);
                },
                "1.6.8",
                cancellationToken =>
                {
                    fullReindexRequests.Add([root]);
                    return indexer.IndexAsync(root, cancellationToken);
                },
                cancellationToken => store.InitializeAsync(cancellationToken),
                null,
                changedFiles is null
                    ? (_, _) => throw new InvalidOperationException("not a git repository")
                    : (_, _) => Task.FromResult(changedFiles));

            return new Fixture
            {
                Server = server,
                Session = session,
                Root = root,
                Store = store,
                ReindexRequests = reindexRequests,
                FullReindexRequests = fullReindexRequests
            };
        }

        private static async Task<string> GetRepositoryIdAsync(string connectionString)
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT Id FROM Repositories LIMIT 1";
            return (string)(await command.ExecuteScalarAsync())!;
        }
    }
}
