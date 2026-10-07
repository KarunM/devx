using DevX.Domain;
using DevX.Infrastructure.Storage.SQLite;
using DevX.Indexer;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevX.Tests.Search;

public sealed class SearchServiceTests
{
    [Fact]
    public async Task SearchSymbolAsync_RanksExactAndPartialMatchesDeterministically()
    {
        var repositoryRoot = Directory.CreateTempSubdirectory("devx-search-test").FullName;
        var projectDirectory = Path.Combine(repositoryRoot, "src", "Sample");
        Directory.CreateDirectory(projectDirectory);

        await System.IO.File.WriteAllTextAsync(Path.Combine(projectDirectory, "Sample.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        await System.IO.File.WriteAllTextAsync(Path.Combine(projectDirectory, "Alpha.cs"), """
            namespace Sample;
            public class Alpha { public void Run() { } }
            """);

        await System.IO.File.WriteAllTextAsync(Path.Combine(projectDirectory, "RunHelper.cs"), """
            namespace Sample;
            public class RunHelper { public void Execute() { } }
            """);

        var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"devx-search-{Guid.NewGuid():N}.db") }.ToString();
        var store = new SqliteStorageInitializer(connectionString);
        var indexer = new RepositoryIndexingService(store);
        await indexer.IndexAsync(repositoryRoot);

        var search = new SearchService(store, store);
        var result = await search.SearchSymbolAsync(new SearchSymbolRequest("Run"));

        Assert.Equal("Run", result.Symbols[0].Name);
        Assert.EndsWith("Alpha.cs", result.Symbols[0].FilePath, StringComparison.Ordinal);
        Assert.Equal(2, result.Symbols[0].StartLine);
        Assert.Contains(result.Symbols, symbol => symbol.Name == "RunHelper");
    }

    [Fact]
    public async Task SearchSymbolAsync_BackfillsMissingLegacyLineRangesFromIndexedFile()
    {
        var repositoryRoot = Directory.CreateTempSubdirectory("devx-search-legacy-lines").FullName;
        var sourcePath = Path.Combine(repositoryRoot, "memory.py");
        await System.IO.File.WriteAllTextAsync(sourcePath, """
            def first():
                return 1

            def _trim_fact(value):
                return value.strip()
            """);
        var databasePath = Path.Combine(Path.GetTempPath(), $"devx-legacy-lines-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        var store = new SqliteStorageInitializer(connectionString);
        await new RepositoryIndexingService(store).IndexAsync(repositoryRoot);

        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE symbols SET start_line = NULL, end_line = NULL WHERE name = '_trim_fact';";
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        var result = await new SearchService(store, store).SearchSymbolAsync(new SearchSymbolRequest("_trim_fact"));
        var symbol = Assert.Single(result.Symbols);
        Assert.Equal(4, symbol.StartLine);
        Assert.Equal(5, symbol.EndLine);

        await using var verification = new SqliteConnection(connectionString);
        await verification.OpenAsync();
        await using var verifyCommand = verification.CreateCommand();
        verifyCommand.CommandText = "SELECT start_line || ':' || end_line FROM symbols WHERE name = '_trim_fact';";
        Assert.Equal("4:5", await verifyCommand.ExecuteScalarAsync());
    }

    [Fact]
    public async Task SearchTextAsync_FindsFilePathsAndMetadata()
    {
        var repositoryRoot = Directory.CreateTempSubdirectory("devx-search-text-test").FullName;
        var projectDirectory = Path.Combine(repositoryRoot, "src", "Sample");
        Directory.CreateDirectory(projectDirectory);

        await System.IO.File.WriteAllTextAsync(Path.Combine(projectDirectory, "Sample.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        await System.IO.File.WriteAllTextAsync(Path.Combine(projectDirectory, "Alpha.cs"), """
            namespace Sample;
            public class Alpha { public void Run() { } }
            """);

        var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"devx-search-text-{Guid.NewGuid():N}.db") }.ToString();
        var store = new SqliteStorageInitializer(connectionString);
        var indexer = new RepositoryIndexingService(store);
        await indexer.IndexAsync(repositoryRoot);

        var search = new SearchService(store, store);
        var result = await search.SearchTextAsync(new SearchTextRequest("Alpha.cs"));

        Assert.Contains(result.Files, file => file.Path.EndsWith("Alpha.cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SearchTextAsync_SearchesFileContentsWithLineAndPreview()
    {
        var repositoryRoot = Directory.CreateTempSubdirectory("devx-content-search").FullName;
        await System.IO.File.WriteAllTextAsync(Path.Combine(repositoryRoot, "notes.md"), "# Notes\nThe hidden needle is here.\n");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"devx-content-{Guid.NewGuid():N}.db") }.ToString();
        var store = new SqliteStorageInitializer(connectionString);
        await new RepositoryIndexingService(store).IndexAsync(repositoryRoot);

        var result = await new SearchService(store, store).SearchTextAsync(new SearchTextRequest("hidden needle"));

        var match = Assert.Single(result.ContentMatches!);
        Assert.Equal(2, match.Line);
        Assert.Contains("hidden needle", match.Preview);
        Assert.Empty(result.Symbols);
    }

    [Theory]
    [InlineData("people")]
    [InlineData("Companions IDs")]
    [InlineData("beta_gate_034_test.py")]
    public async Task SearchTextAsync_FindsReportedWorkflowQueries(string query)
    {
        var repositoryRoot = Directory.CreateTempSubdirectory("devx-reported-searches").FullName;
        await System.IO.File.WriteAllTextAsync(Path.Combine(repositoryRoot, "notes.md"), "people\nCompanions IDs\nbeta_gate_034_test.py\n");
        var store = new SqliteStorageInitializer(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"devx-reported-{Guid.NewGuid():N}.db") }.ToString());
        await new RepositoryIndexingService(store).IndexAsync(repositoryRoot);

        var result = await new SearchService(store, store).SearchTextAsync(new SearchTextRequest(query));

        Assert.Contains(result.ContentMatches!, match => match.Preview.Contains(query, StringComparison.OrdinalIgnoreCase));
    }
}
