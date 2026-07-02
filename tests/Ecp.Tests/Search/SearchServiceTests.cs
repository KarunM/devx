using Ecp.Domain;
using Ecp.Infrastructure.Storage.SQLite;
using Ecp.Indexer;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Ecp.Tests.Search;

public sealed class SearchServiceTests
{
    [Fact]
    public async Task SearchSymbolAsync_RanksExactAndPartialMatchesDeterministically()
    {
        var repositoryRoot = Directory.CreateTempSubdirectory("ecp-search-test").FullName;
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

        var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"ecp-search-{Guid.NewGuid():N}.db") }.ToString();
        var store = new SqliteStorageInitializer(connectionString);
        var indexer = new RepositoryIndexingService(store);
        await indexer.IndexAsync(repositoryRoot);

        var search = new SearchService(store, store);
        var result = await search.SearchSymbolAsync(new SearchSymbolRequest("Run"));

        Assert.Equal("Run", result.Symbols[0].Name);
        Assert.Contains(result.Symbols, symbol => symbol.Name == "RunHelper");
    }

    [Fact]
    public async Task SearchTextAsync_FindsFilePathsAndMetadata()
    {
        var repositoryRoot = Directory.CreateTempSubdirectory("ecp-search-text-test").FullName;
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

        var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"ecp-search-text-{Guid.NewGuid():N}.db") }.ToString();
        var store = new SqliteStorageInitializer(connectionString);
        var indexer = new RepositoryIndexingService(store);
        await indexer.IndexAsync(repositoryRoot);

        var search = new SearchService(store, store);
        var result = await search.SearchTextAsync(new SearchTextRequest("Alpha.cs"));

        Assert.Contains(result.Files, file => file.Path.EndsWith("Alpha.cs", StringComparison.Ordinal));
    }
}
