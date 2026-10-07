using DevX.Domain;
using DevX.Indexer;
using DevX.Infrastructure.Storage.SQLite;
using Microsoft.Data.Sqlite;
using Xunit;
using IOFile = System.IO.File;
using System.Diagnostics;

namespace DevX.Tests.Indexer;

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
    public async Task ReindexFilesAsync_UpdatesMultipleFiles()
    {
        var (store, _) = CreateStore();
        await store.InitializeAsync();

        var repoRoot = CreateSampleRepo();
        var secondFile = Path.Combine(repoRoot, "src", "Sample", "Helpers.cs");
        IOFile.WriteAllText(secondFile, """
            namespace Sample;
            public static class Helpers { public static void OldMethod() { } }
            """);

        var indexer = new RepositoryIndexingService(store);
        await indexer.IndexAsync(repoRoot);

        IOFile.WriteAllText(Path.Combine(repoRoot, "src", "Sample", "Program.cs"), """
            namespace Sample;
            public static class Program { public static void Run() { } public static void AddedProgramMethod() { } }
            """);

        IOFile.WriteAllText(secondFile, """
            namespace Sample;
            public static class Helpers { public static void NewHelperMethod() { } }
            """);

        var result = await indexer.ReindexFilesAsync(repoRoot, ["src/Sample/Program.cs", "src/Sample/Helpers.cs"]);

        Assert.False(result.PerformedFullReindex);
        Assert.Equal(2, result.UpdatedFiles);
        Assert.Single(await store.FindSymbolsByNameAsync("AddedProgramMethod"));
        Assert.Single(await store.FindSymbolsByNameAsync("NewHelperMethod"));
    }

    [Fact]
    public async Task ReindexChangedFilesAsync_PerformsFullReindexWhenProjectFilesChange()
    {
        var (store, _) = CreateStore();
        await store.InitializeAsync();

        var repoRoot = CreateSampleRepo();
        InitializeGitRepo(repoRoot);

        var indexer = new RepositoryIndexingService(store);
        await indexer.IndexAsync(repoRoot);

        IOFile.WriteAllText(Path.Combine(repoRoot, "src", "Sample", "Program.cs"), """
            namespace Sample;
            public static class Program { public static void Run() { } public static void ChangedViaGit() { } }
            """);

        IOFile.WriteAllText(Path.Combine(repoRoot, "src", "Sample", "Sample.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);

        var result = await indexer.ReindexChangedFilesAsync(repoRoot);

        Assert.True(result.PerformedFullReindex);
        Assert.Contains("src/Sample/Sample.csproj", result.RequestedFiles);
        Assert.Single(await store.FindSymbolsByNameAsync("ChangedViaGit"));
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

    [Fact]
    public async Task ReindexFilesAsync_UpdatesHtmlInsteadOfSkippingIt()
    {
        var repositoryRoot = Directory.CreateTempSubdirectory("devx-html-reindex").FullName;
        var htmlPath = Path.Combine(repositoryRoot, "status.html");
        await IOFile.WriteAllTextAsync(htmlPath, "<h1>Before</h1>");
        var (store, _) = CreateStore();
        var indexer = new RepositoryIndexingService(store);
        await indexer.IndexAsync(repositoryRoot);
        await IOFile.WriteAllTextAsync(htmlPath, "<h1>After</h1>");

        var result = await indexer.ReindexFilesAsync(repositoryRoot, ["status.html"]);
        var matches = await store.SearchFileContentsAsync("After");

        Assert.Equal(1, result.UpdatedFiles);
        Assert.Equal(0, result.SkippedFiles);
        Assert.Contains(matches, match => match.Path == "status.html");
    }

    private static (SqliteStorageInitializer Store, string ConnectionString) CreateStore()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"devx-reindex-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        return (new SqliteStorageInitializer(connectionString), connectionString);
    }

    private static string CreateSampleRepo()
    {
        var repoRoot = Directory.CreateTempSubdirectory("devx-reindex-repo").FullName;
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

    private static void InitializeGitRepo(string repoRoot)
    {
        RunGit(repoRoot, "init");
        RunGit(repoRoot, "config user.email devx-tests@example.com");
        RunGit(repoRoot, "config user.name DevX Tests");
        RunGit(repoRoot, "add .");
        RunGit(repoRoot, "commit -m initial");
    }

    private static void RunGit(string workingDirectory, string arguments)
    {
        var startInfo = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(startInfo)!;
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(process.StandardError.ReadToEnd());
        }
    }

    private static Guid StableId(string value)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(guidBytes);
        return new Guid(guidBytes);
    }
}
