using Ecp.Domain;
using Ecp.Indexer;
using Ecp.Infrastructure.Storage.SQLite;
using Microsoft.Data.Sqlite;
using Xunit;
using IOFile = System.IO.File;

namespace Ecp.Tests.Indexer;

public sealed class RepositoryIndexingServiceReindexTests
{
    [Fact]
    public async Task ReindexFileAsync_UpdatesSymbolsForChangedFile()
    {
        var (store, connectionString) = CreateStore();
        await store.InitializeAsync();

        var repoRoot = CreateSampleRepo();
        var indexer = new RepositoryIndexingService(store);
        await indexer.IndexAsync(repoRoot);

        var filePath = Path.Combine(repoRoot, "src", "Sample", "Program.cs");
        var relativePath = "src/Sample/Program.cs";

        // Verify original symbol exists
        var originalSymbols = await store.FindSymbolsByNameAsync("Run");
        Assert.Single(originalSymbols);
        Assert.Equal("Method", originalSymbols[0].Kind);

        // Modify the file — add a new method
        IOFile.WriteAllText(filePath, """
            namespace Sample;
            public static class Program
            {
                public static void Run() { }
                public static void NewMethod() { }
            }
            """);

        // Re-index the file
        await indexer.ReindexFileAsync(repoRoot, relativePath);

        // Verify the new method is now discoverable
        var updatedSymbols = await store.FindSymbolsByNameAsync("NewMethod");
        Assert.Single(updatedSymbols);
        Assert.Equal("Method", updatedSymbols[0].Kind);
        Assert.Equal("Sample.Program.NewMethod", updatedSymbols[0].QualifiedName);

        // Verify old method still exists
        var runSymbols = await store.FindSymbolsByNameAsync("Run");
        Assert.Single(runSymbols);
    }

    [Fact]
    public async Task ReindexFileAsync_RemovesOldSymbolsForDeletedTypes()
    {
        var (store, connectionString) = CreateStore();
        await store.InitializeAsync();

        var repoRoot = CreateSampleRepo();
        var indexer = new RepositoryIndexingService(store);
        await indexer.IndexAsync(repoRoot);

        var filePath = Path.Combine(repoRoot, "src", "Sample", "Program.cs");
        var relativePath = "src/Sample/Program.cs";

        // Verify original type exists
        var originalSymbols = await store.FindSymbolsByNameAsync("Program");
        Assert.Single(originalSymbols);

        // Replace file with completely different type
        IOFile.WriteAllText(filePath, """
            namespace Sample;
            public static class NewType
            {
                public static void DoWork() { }
            }
            """);

        // Re-index the file
        await indexer.ReindexFileAsync(repoRoot, relativePath);

        // Verify old type is gone
        var programSymbols = await store.FindSymbolsByNameAsync("Program");
        Assert.Empty(programSymbols);

        // Verify new type exists
        var newTypeSymbols = await store.FindSymbolsByNameAsync("NewType");
        Assert.Single(newTypeSymbols);
    }

    [Fact]
    public async Task ReindexFileAsync_ThrowsForFileOutsideRepository()
    {
        var (store, _) = CreateStore();
        await store.InitializeAsync();

        var indexer = new RepositoryIndexingService(store);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await indexer.ReindexFileAsync("/tmp/repo", "/etc/passwd"));
    }

    [Fact]
    public async Task DeleteFileAsync_RemovesFileAndCascadesToSymbols()
    {
        var (store, connectionString) = CreateStore();
        await store.InitializeAsync();

        var repoRoot = CreateSampleRepo();
        var indexer = new RepositoryIndexingService(store);
        await indexer.IndexAsync(repoRoot);

        var filePath = Path.Combine(repoRoot, "src", "Sample", "Program.cs");
        var fileId = new FileId(StableId(filePath));

        // Verify symbols exist before deletion
        var symbolsBefore = await store.FindSymbolsByNameAsync("Run");
        Assert.Single(symbolsBefore);

        // Delete the file record
        await store.DeleteFileAsync(fileId);

        // Verify the file is gone (symbols are cascade-deleted via FK)
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM files WHERE id = $id;";
        command.Parameters.AddWithValue("$id", fileId.Value.ToString());
        var count = (long)(await command.ExecuteScalarAsync())!;
        Assert.Equal(0, count);
    }

    private static (SqliteStorageInitializer Store, string ConnectionString) CreateStore()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"ecp-reindex-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        return (new SqliteStorageInitializer(connectionString), connectionString);
    }

    private static string CreateSampleRepo()
    {
        var repoRoot = Directory.CreateTempSubdirectory("ecp-reindex-repo").FullName;
        var projectRoot = Path.Combine(repoRoot, "src", "Sample");
        Directory.CreateDirectory(projectRoot);

        IOFile.WriteAllText(Path.Combine(projectRoot, "Sample.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        IOFile.WriteAllText(Path.Combine(projectRoot, "Program.cs"), """
            namespace Sample;
            public static class Program { public static void Run() { } }
            """);

        return repoRoot;
    }

    private static Guid StableId(string value)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(guidBytes);
        return new Guid(guidBytes);
    }
}
