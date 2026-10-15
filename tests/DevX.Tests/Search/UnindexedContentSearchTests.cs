using System.Diagnostics;
using System.Text.Json;
using DevX.Cli;
using DevX.Domain;
using DevX.Indexer;
using DevX.Infrastructure.Storage.SQLite;
using Microsoft.Data.Sqlite;
using Xunit;
using IOFile = System.IO.File;

namespace DevX.Tests.Search;

public sealed class UnindexedContentSearchTests
{
    private const string Marker = "ValidatePoliciesWithAccessAnalyzer";

    private static async Task<(string Root, SqliteStorageInitializer Store)> Create(params (string Path, string Source)[] files)
    {
        var root = Directory.CreateTempSubdirectory("devx-unindexed-").FullName;
        foreach (var (path, source) in files)
        {
            var full = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await IOFile.WriteAllTextAsync(full, source);
        }

        var git = Process.Start(new ProcessStartInfo("git", "init -q") { WorkingDirectory = root, RedirectStandardError = true, RedirectStandardOutput = true });
        git!.WaitForExit();
        var db = Path.Combine(Path.GetTempPath(), $"devx-unindexed-{Guid.NewGuid():N}.db");
        var store = new SqliteStorageInitializer(new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ToString());
        await new RepositoryIndexingService(store).IndexAsync(root);
        return (root, store);
    }

    [Fact]
    public async Task FindsTextInTrackedFilesWithNoSymbolIndex_HiddenDirsIncluded_IgnoredAndBinaryExcluded()
    {
        var (_, store) = await Create(
            ("app.cs", "class A { }\n"),
            ("notes.txt", $"see {Marker} here\n"),
            (".github/CODEOWNERS", $"# {Marker}\n"),
            ("Makefile", $"all:\n\techo {Marker}\n"),
            (".gitignore", "ignored/\n"),
            ("ignored/skip.txt", $"{Marker}\n"));
        var outcome = await store.SearchFileContentsWithCoverageAsync(Marker);

        Assert.Equal([".github/CODEOWNERS", "Makefile", "notes.txt"], outcome.Matches.Select(m => m.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray());
        Assert.All(outcome.Matches, m => Assert.Equal(string.Empty, m.FileId));
        Assert.Equal(3, outcome.Coverage.UnindexedHits);
        Assert.Contains("3 match(es) are in files without a symbol index", outcome.Coverage.Scope);
        Assert.Contains(".gitignore honored", outcome.Coverage.Scope);
    }

    [Fact]
    public async Task BinaryFilesAreSkipped()
    {
        var (root, store) = await Create(("notes.txt", "plain\n"));
        await IOFile.WriteAllBytesAsync(Path.Combine(root, "blob.bin"), [0, 1, 2, .. System.Text.Encoding.ASCII.GetBytes(Marker)]);
        var outcome = await store.SearchFileContentsWithCoverageAsync(Marker);
        Assert.Empty(outcome.Matches);
        Assert.NotNull(outcome.Coverage.Scope);
    }

    [Fact]
    public async Task TerraformFileIsIndexedAndSearchable_WithItsFileId()
    {
        var (_, store) = await Create(("infra/bootstrap/main.tf", $"resource \"a\" \"b\" {{\n  x = 1\n  # {Marker}\n}}\n"));
        var outcome = await store.SearchFileContentsWithCoverageAsync(Marker);
        var hit = Assert.Single(outcome.Matches);
        Assert.Equal(("infra/bootstrap/main.tf", 3), (hit.Path, hit.Line));
        Assert.NotEqual(string.Empty, hit.FileId);
        Assert.Equal(0, outcome.Coverage.UnindexedHits);
        Assert.True(outcome.Coverage.Complete);
    }

    [Fact]
    public void CoverageLineStatesWhatWasSearched_EvenWhenComplete()
    {
        using var complete = JsonDocument.Parse("""{"contentCoverage":{"returnedCount":1,"outputTruncated":false,"complete":true,"scanCapped":false,"warnings":[],"scope":"every non-ignored text file"}}""");
        using var unknown = JsonDocument.Parse("""{"contentCoverage":{"returnedCount":0,"outputTruncated":null,"complete":null,"scanCapped":false,"warnings":["w"],"scope":"S"}}""");
        Assert.Equal("Searched: every non-ignored text file.", ContentSearchCoverageFormatter.Format(complete.RootElement));
        Assert.Equal("Search coverage unknown; results may be incomplete. w Searched: S.", ContentSearchCoverageFormatter.Format(unknown.RootElement));
    }
}
