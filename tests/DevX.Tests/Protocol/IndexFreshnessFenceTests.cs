using System.Text.Json;
using DevX.Application;
using DevX.Domain;
using DevX.Infrastructure.Storage.SQLite;
using DevX.Indexer;
using DevX.Protocol;
using Microsoft.Data.Sqlite;
using Xunit;
using IOFile = System.IO.File;

namespace DevX.Tests.Protocol;

public sealed class IndexFreshnessFenceTests
{
    [Fact]
    public async Task DirtyFileFenceSurvivesStoreReconstructionAndClearsOnlyAfterFileRefresh()
    {
        var root = Directory.CreateTempSubdirectory("devx-dirty-restart").FullName;
        var connection = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"devx-dirty-{Guid.NewGuid():N}.db") }.ToString();
        var first = new SqliteStorageInitializer(connection);
        await first.InitializeAsync();
        Assert.True(await first.TryMarkIndexFilesDirtyAsync(root, ["src/a.cs"]));

        var reconstructed = new SqliteStorageInitializer(connection);
        await reconstructed.InitializeAsync();
        var dirty = await reconstructed.GetDirtyIndexRepositoryAsync(root);
        Assert.NotNull(dirty);
        Assert.Equal(["src/a.cs"], dirty!.DirtyFiles);
        await reconstructed.ClearDirtyIndexFilesAsync(root, ["src/a.cs"]);
        Assert.Null(await first.GetDirtyIndexRepositoryAsync(root));
    }

    [Fact]
    public async Task FullReindexFenceCannotBeClearedByOneFileRefresh()
    {
        var root = Directory.CreateTempSubdirectory("devx-dirty-full").FullName;
        var connection = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"devx-dirty-{Guid.NewGuid():N}.db") }.ToString();
        var store = new SqliteStorageInitializer(connection);
        await store.InitializeAsync();
        await store.MarkIndexRepositoryDirtyAsync(root, ["src/a.cs", "src/b.cs"]);
        await store.ClearDirtyIndexFilesAsync(root, ["src/a.cs"]);

        var dirty = await store.GetDirtyIndexRepositoryAsync(root);
        Assert.NotNull(dirty);
        Assert.True(dirty!.RequiresFullReindex);
        Assert.Equal(["src/a.cs", "src/b.cs"], dirty.DirtyFiles);
    }

    [Fact]
    public async Task IndexedProtocolReadsFailClosedUntilFullReconciliation()
    {
        var root = Directory.CreateTempSubdirectory("devx-dirty-query").FullName;
        var project = Path.Combine(root, "src", "Sample");
        Directory.CreateDirectory(project);
        await IOFile.WriteAllTextAsync(Path.Combine(project, "Sample.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        await IOFile.WriteAllTextAsync(Path.Combine(project, "Alpha.cs"), "namespace Sample;\npublic class Alpha\n{\n    public void Before()\n    {\n    }\n}");
        var connection = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"devx-dirty-query-{Guid.NewGuid():N}.db") }.ToString();
        var store = new SqliteStorageInitializer(connection);
        await new RepositoryIndexingService(store).IndexAsync(root);
        await using var connectionDb = new SqliteConnection(connection);
        await connectionDb.OpenAsync();
        await using var command = connectionDb.CreateCommand();
        command.CommandText = "SELECT id FROM repositories WHERE root_path = $root;";
        command.Parameters.AddWithValue("$root", Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)));
        var repositoryId = new RepositoryId(Guid.Parse((string)(await command.ExecuteScalarAsync())!));
        var session = await store.CreateSessionAsync(new CreateSessionRequest(repositoryId));
        var engine = new ProtocolEngine(new SearchService(store, store), store, store);

        Assert.True(await store.TryMarkIndexFilesDirtyAsync(root, ["src/Sample/Alpha.cs"]));
        var blocked = await engine.ExecuteAsync(new ProtocolRequest("1.0", ProtocolOperations.SearchSymbol,
            session.Id.Value.ToString(), JsonSerializer.SerializeToElement(new { query = "Before" })));
        Assert.False(blocked.Success);
        Assert.Contains(blocked.Errors, error => error.Code == "IndexDirty");

        await new RepositoryIndexingService(store).IndexAsync(root);
        var reconciled = await engine.ExecuteAsync(new ProtocolRequest("1.0", ProtocolOperations.SearchSymbol,
            session.Id.Value.ToString(), JsonSerializer.SerializeToElement(new { query = "Before" })));
        Assert.True(reconciled.Success);
    }

    [Fact]
    public async Task FailedPatchRefreshBlocksReadMethodAndFullReindexReconcilesRemovedSymbols()
    {
        var root = Directory.CreateTempSubdirectory("devx-dirty-method").FullName;
        var project = Path.Combine(root, "src", "Sample");
        Directory.CreateDirectory(project);
        await IOFile.WriteAllTextAsync(Path.Combine(project, "Sample.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        await IOFile.WriteAllTextAsync(Path.Combine(project, "Alpha.cs"), "namespace Sample;\npublic class Alpha\n{\n    public void Before() { }\n}");
        var connection = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"devx-dirty-method-{Guid.NewGuid():N}.db") }.ToString();
        var store = new SqliteStorageInitializer(connection);
        var indexer = new RepositoryIndexingService(store);
        await indexer.IndexAsync(root);
        await using var database = new SqliteConnection(connection);
        await database.OpenAsync();
        await using var repositoryQuery = database.CreateCommand();
        repositoryQuery.CommandText = "SELECT id FROM repositories WHERE root_path = $root;";
        repositoryQuery.Parameters.AddWithValue("$root", Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)));
        var session = await store.CreateSessionAsync(new CreateSessionRequest(new RepositoryId(Guid.Parse((string)(await repositoryQuery.ExecuteScalarAsync())!))));
        var search = new SearchService(store, store);
        var before = await search.SearchSymbolAsync(new SearchSymbolRequest("Before"));
        var beforeId = Assert.Single(before.Symbols).Id;

        await using (var trigger = database.CreateCommand())
        {
            trigger.CommandText = "CREATE TRIGGER fail_index_refresh BEFORE DELETE ON files BEGIN SELECT RAISE(ABORT, 'controlled refresh failure'); END;";
            await trigger.ExecuteNonQueryAsync();
        }
        var patch = JsonSerializer.SerializeToElement(new
        {
            version = "1.0",
            operations = new[] { new { operation = "ReplaceText", path = "src/Sample/Alpha.cs", oldText = "Before", replacementText = "After" } }
        });
        var engine = new ProtocolEngine(search, store, store);
        var applied = await engine.ExecuteAsync(new ProtocolRequest("1.0", ProtocolOperations.Patch, session.Id.Value.ToString(), patch));
        Assert.False(applied.Success);
        Assert.Contains(applied.Errors, error => error.Code == "IndexRefreshFailedDirty");
        Assert.Contains("After", await IOFile.ReadAllTextAsync(Path.Combine(project, "Alpha.cs")));

        var staleDirectly = await search.SearchSymbolAsync(new SearchSymbolRequest("Before"));
        Assert.Contains(staleDirectly.Symbols, symbol => symbol.Id == beforeId);
        var blockedRead = await engine.ExecuteAsync(new ProtocolRequest("1.0", "READ_METHOD", session.Id.Value.ToString(),
            JsonSerializer.SerializeToElement(new { id = beforeId })));
        Assert.False(blockedRead.Success);
        Assert.Contains(blockedRead.Errors, error => error.Code == "IndexDirty");

        await using (var drop = database.CreateCommand())
        {
            drop.CommandText = "DROP TRIGGER fail_index_refresh;";
            await drop.ExecuteNonQueryAsync();
        }
        await indexer.IndexAsync(root);
        Assert.Null(await store.GetDirtyIndexRepositoryAsync(root));
        var reconciled = await search.SearchSymbolAsync(new SearchSymbolRequest("Before"));
        Assert.DoesNotContain(reconciled.Symbols, symbol => symbol.Id == beforeId);
        Assert.NotEmpty((await search.SearchSymbolAsync(new SearchSymbolRequest("After"))).Symbols);
    }

    [Fact]
    public async Task UncertainPartialRollbackKeepsIndexedReadsFenced()
    {
        var root = Directory.CreateTempSubdirectory("devx-dirty-rollback").FullName;
        var project = Path.Combine(root, "src", "Sample");
        Directory.CreateDirectory(project);
        await IOFile.WriteAllTextAsync(Path.Combine(project, "Sample.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        await IOFile.WriteAllTextAsync(Path.Combine(project, "Alpha.cs"), "namespace Sample; public class Alpha { public void After() { } }");
        Directory.CreateDirectory(Path.Combine(root, "Blocked.txt"));

        var connection = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"devx-dirty-rollback-{Guid.NewGuid():N}.db") }.ToString();
        var store = new SqliteStorageInitializer(connection);
        var indexer = new RepositoryIndexingService(store);
        await indexer.IndexAsync(root);
        await using var database = new SqliteConnection(connection);
        await database.OpenAsync();
        await using var repositoryQuery = database.CreateCommand();
        repositoryQuery.CommandText = "SELECT id FROM repositories WHERE root_path = $root;";
        repositoryQuery.Parameters.AddWithValue("$root", Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)));
        var session = await store.CreateSessionAsync(new CreateSessionRequest(new RepositoryId(Guid.Parse((string)(await repositoryQuery.ExecuteScalarAsync())!))));
        var search = new SearchService(store, store);
        var indexedAfter = Assert.Single((await search.SearchSymbolAsync(new SearchSymbolRequest("After"))).Symbols);
        var engine = new ProtocolEngine(search, store, store);

        // Rollback walks operations in reverse: it restores Alpha.cs first, then encounters
        // a directory where Blocked.txt should be, making the overall outcome uncertain.
        var patch = JsonSerializer.SerializeToElement(new
        {
            version = "1.0",
            operations = new object[]
            {
                new { operation = "ReplaceText", path = "Blocked.txt", oldText = "before", replacementText = "after", beforeState = "before" },
                new { operation = "ReplaceText", path = "src/Sample/Alpha.cs", oldText = "Before", replacementText = "After", beforeState = "namespace Sample;\npublic class Alpha\n{\n    public void Before() { }\n}" }
            }
        });

        var rollback = await engine.ExecuteAsync(new ProtocolRequest("1.0", ProtocolOperations.PatchRollback,
            session.Id.Value.ToString(), patch));

        Assert.False(rollback.Success);
        Assert.Contains(rollback.Errors, error => error.Code == "PatchRollbackOutcomeUncertain");
        Assert.Contains("Before", await IOFile.ReadAllTextAsync(Path.Combine(project, "Alpha.cs")));
        var blocked = await engine.ExecuteAsync(new ProtocolRequest("1.0", "READ_METHOD", session.Id.Value.ToString(),
            JsonSerializer.SerializeToElement(new { id = indexedAfter.Id })));
        Assert.False(blocked.Success);
        Assert.Contains(blocked.Errors, error => error.Code == "IndexDirty");
        var dirty = await store.GetDirtyIndexRepositoryAsync(root);
        Assert.NotNull(dirty);
        Assert.True(dirty!.RequiresFullReindex);
    }

    [Fact]
    public async Task UnsupportedExtensionCanClearItsFileFenceButLooseCSharpRemainsDirty()
    {
        var root = Directory.CreateTempSubdirectory("devx-dirty-unsupported").FullName;
        var project = Path.Combine(root, "src", "Sample");
        Directory.CreateDirectory(project);
        await IOFile.WriteAllTextAsync(Path.Combine(project, "Sample.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        await IOFile.WriteAllTextAsync(Path.Combine(project, "notes.txt"), "plain text");
        var connection = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"devx-dirty-unsupported-{Guid.NewGuid():N}.db") }.ToString();
        var store = new SqliteStorageInitializer(connection);
        var indexer = new RepositoryIndexingService(store);
        await indexer.IndexAsync(root);

        Assert.True(await store.TryMarkIndexFilesDirtyAsync(root, ["src/Sample/notes.txt"]));
        var unsupported = await indexer.ReindexFilesAsync(root, ["src/Sample/notes.txt"]);
        Assert.Equal(0, unsupported.UpdatedFiles);
        Assert.Equal(1, unsupported.SkippedFiles);
        Assert.DoesNotContain("ERROR:", Assert.Single(unsupported.SkippedFilePaths).Reason);
        Assert.Null(await store.GetDirtyIndexRepositoryAsync(root));
        await using var database = new SqliteConnection(connection);
        await database.OpenAsync();
        await using (var fileQuery = database.CreateCommand())
        {
            fileQuery.CommandText = "SELECT COUNT(*) FROM files WHERE path = 'src/Sample/notes.txt';";
            Assert.Equal(0L, (long)(await fileQuery.ExecuteScalarAsync())!);
        }

        var looseRoot = Directory.CreateTempSubdirectory("devx-dirty-loose-csharp").FullName;
        await IOFile.WriteAllTextAsync(Path.Combine(looseRoot, "Loose.cs"), "namespace Sample; public class Loose { public void Run() { } }");
        await IOFile.WriteAllTextAsync(Path.Combine(looseRoot, "notes.md"), "notes");
        var looseIndexer = new RepositoryIndexingService(store);
        await looseIndexer.IndexAsync(looseRoot);
        Assert.True(await store.TryMarkIndexFilesDirtyAsync(looseRoot, ["notes.md", "Loose.cs"]));
        var loose = await looseIndexer.ReindexFilesAsync(looseRoot, ["notes.md", "Loose.cs"]);
        Assert.Equal(1, loose.UpdatedFiles);
        Assert.Equal(1, loose.SkippedFiles);
        Assert.StartsWith("ERROR:", Assert.Single(loose.SkippedFilePaths).Reason, StringComparison.Ordinal);
        var dirty = await store.GetDirtyIndexRepositoryAsync(looseRoot);
        Assert.NotNull(dirty);
        Assert.Equal(["Loose.cs"], dirty!.DirtyFiles);
    }
}
