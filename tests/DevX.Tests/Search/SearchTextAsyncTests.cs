using DevX.Domain;
using DevX.Infrastructure.Storage.SQLite;
using DevX.Indexer;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevX.Tests.Search;

/// <summary>Brief OSS12 item 3, the owner's contract: `devx search text` gets full regex and
/// `a|b` alternation, and the answer must not depend on whether ripgrep happens to be installed.
/// These go through the storage layer, so they hold with ripgrep on PATH (the fast path) and
/// without it (the in-process scan) -- and on this machine there is no rg, so they really do
/// exercise the fallback.</summary>
public sealed class SearchTextAsyncTests
{
    private static async Task<SearchService> IndexedAsync(params (string Name, string Body)[] files)
    {
        var repositoryRoot = Directory.CreateTempSubdirectory("devx-search-text").FullName;
        await System.IO.File.WriteAllTextAsync(Path.Combine(repositoryRoot, "Sample.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        foreach (var file in files)
        {
            await System.IO.File.WriteAllTextAsync(Path.Combine(repositoryRoot, file.Name), file.Body);
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(Path.GetTempPath(), $"devx-search-text-{Guid.NewGuid():N}.db"),
        }.ToString();
        var store = new SqliteStorageInitializer(connectionString);
        await new RepositoryIndexingService(store).IndexAsync(repositoryRoot);
        return new SearchService(store, store);
    }

    [Fact]
    public async Task SearchTextAsync_MatchesARegexNotJustALiteral()
    {
        var search = await IndexedAsync(
            ("Sample.cs", "namespace Sample;\npublic class Sample {\n  void LoadLevel() { }\n  void LoadMoniker() { }\n}\n"));

        // `Load(Level|Moniker)` is not a substring of anything in the file, so a literal scan
        // returns nothing. Agents wrote queries like this and were told "no matches".
        var result = await search.SearchTextAsync(new SearchTextRequest("Load(Level|Moniker)"));

        Assert.NotNull(result.ContentMatches);
        Assert.Equal(2, result.ContentMatches!.Count);
    }

    [Fact]
    public async Task SearchTextAsync_MatchesAlternation()
    {
        var search = await IndexedAsync(
            ("Sample.cs", "namespace Sample;\npublic class Sample {\n  void LoadLevel() { }\n  int Minimum = 3;\n}\n"));

        var result = await search.SearchTextAsync(new SearchTextRequest("LoadLevel|Minimum"));

        Assert.Equal(2, result.ContentMatches!.Count);
    }

    [Fact]
    public async Task SearchTextAsync_StaysCaseInsensitiveForAPlainWord()
    {
        var search = await IndexedAsync(("Sample.cs", "namespace Sample;\npublic class Sample { int Minimum = 3; }\n"));

        var result = await search.SearchTextAsync(new SearchTextRequest("minimum"));

        Assert.Single(result.ContentMatches!);
    }

    [Fact]
    public async Task SearchTextAsync_FallsBackToALiteralWhenThePatternWillNotCompile()
    {
        var search = await IndexedAsync(("Sample.cs", "namespace Sample;\npublic class Sample { int X = list.GetValueOrDefault(key); }\n"));

        // An unbalanced `(` -- a query an agent really typed. Empty here would have been
        // indistinguishable from "this is not in the repository".
        var result = await search.SearchTextAsync(new SearchTextRequest("GetValueOrDefault("));

        Assert.Single(result.ContentMatches!);
    }
}
