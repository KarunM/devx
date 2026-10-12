using System.Reflection;
using File = System.IO.File;
using DevX.Cli;
using DevX.Domain;
using DevX.Infrastructure.Storage.SQLite;
using DevX.Indexer;
using DevX.Protocol;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using Xunit;

namespace DevX.Tests.Cli;

[Collection("ProcessGlobalState")]
public sealed class ReplaceCommandTests
{
    private static readonly SemaphoreSlim StateLock = new(1, 1);

    [Fact] public async Task Replace_UniqueMatch_ReturnsZeroAndChangesFile() => await RunCase("one", ["--old", "one", "--new", "two"], 0, "two");
    [Fact] public async Task Replace_AmbiguousMatch_ReturnsOne() => await RunCase("one one", ["--old", "one", "--new", "two"], 1, "one one", ambiguous: true);
    [Fact] public async Task Replace_MissingMatch_ReturnsOne() => await RunCase("stable", ["--old", "missing", "--new", "two"], 1, "stable", missing: true);
    [Fact] public async Task Replace_All_ReplacesEveryOccurrence() => await RunCase("one one", ["--old", "one", "--new", "two", "--all"], 0, "two two");
    [Fact] public async Task Replace_Occurrence_ReplacesSelectedOccurrence() => await RunCase("one one", ["--old", "one", "--new", "two", "--occurrence", "2"], 0, "one two");

    [Fact]
    public async Task Replace_VerifiesOnlyWhenAskedAndAnswersInOneLine()
    {
        await StateLock.WaitAsync(); var oldOut = Console.Out;
        var root = Directory.CreateTempSubdirectory("devx-cli-replace-verify").FullName;
        var connection = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"devx-cli-replace-verify-{Guid.NewGuid():N}.db") }.ToString();
        var store = new SqliteStorageInitializer(connection);
        var indexer = new RepositoryIndexingService(store);
        async Task Reindex(string file)
        {
            var refreshed = await indexer.ReindexFilesAsync(root, [file]);
            if (refreshed.SkippedFiles > 0) throw new InvalidOperationException("Index refresh was incomplete.");
        }
        try
        {
            await store.InitializeAsync();
            System.IO.File.WriteAllText(Path.Combine(root, "sample.md"), "old");
            await indexer.IndexAsync(root);
            var output = new StringWriter(); Console.SetOut(output);
            var defaultExit = await ReplaceCommand.RunAsync(["replace", "sample.md", "--old", "old", "--new", "new"], root, reindexFile: Reindex, freshnessStore: store);
            Assert.Equal(0, defaultExit);
            Assert.DoesNotContain("PASS edit", output.ToString());

            System.IO.File.WriteAllText(Path.Combine(root, "sample.md"), "old");
            await indexer.IndexAsync(root);
            output.GetStringBuilder().Clear();
            var verifiedExit = await ReplaceCommand.RunAsync(["replace", "sample.md", "--old", "old", "--new", "new", "--verify"], root, FakeVerifier, Reindex, store);
            Assert.Equal(0, verifiedExit);
            // 1.6.14: ONE line for a passing verification, not a build: line and a tests: line.
            Assert.Contains("PASS edit — PASS tests", output.ToString());
            Assert.DoesNotContain("build:", output.ToString());

            System.IO.File.WriteAllText(Path.Combine(root, "sample.md"), "old");
            await indexer.IndexAsync(root);
            output.GetStringBuilder().Clear();
            var buildOnlyExit = await ReplaceCommand.RunAsync(["replace", "sample.md", "--old", "old", "--new", "new", "--verify=build"], root, FakeVerifier, Reindex, store);
            Assert.Equal(0, buildOnlyExit);
            Assert.Contains("PASS edit", output.ToString());
            Assert.DoesNotContain("PASS tests", output.ToString());

            System.IO.File.WriteAllText(Path.Combine(root, "sample.md"), "old");
            await indexer.IndexAsync(root);
            output.GetStringBuilder().Clear();
            var noVerifyExit = await ReplaceCommand.RunAsync(["replace", "sample.md", "--old", "old", "--new", "new", "--verify", "--no-verify"], root, reindexFile: Reindex, freshnessStore: store);
            Assert.Equal(0, noVerifyExit);
            Assert.DoesNotContain("PASS edit", output.ToString());
        }
        finally { Console.SetOut(oldOut); Directory.Delete(root, true); StateLock.Release(); }
    }

    [Fact]
    public async Task Replace_WhenIncrementalRefreshFails_ReportsAppliedEditAndLeavesIndexedReadsBlocked()
    {
        await StateLock.WaitAsync();
        var oldOut = Console.Out;
        var oldError = Console.Error;
        var root = Directory.CreateTempSubdirectory("devx-cli-replace-refresh-failure").FullName;
        var connection = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"devx-cli-replace-refresh-failure-{Guid.NewGuid():N}.db") }.ToString();
        try
        {
            var file = Path.Combine(root, "notes.md");
            File.WriteAllText(file, "old");
            var store = new SqliteStorageInitializer(connection);
            var indexer = new RepositoryIndexingService(store);
            await indexer.IndexAsync(root);
            await using var database = new SqliteConnection(connection);
            await database.OpenAsync();
            await using var command = database.CreateCommand();
            command.CommandText = "SELECT id FROM repositories WHERE root_path = $root;";
            command.Parameters.AddWithValue("$root", Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)));
            var session = await store.CreateSessionAsync(new CreateSessionRequest(new RepositoryId(Guid.Parse((string)(await command.ExecuteScalarAsync())!))));
            await using (var trigger = database.CreateCommand())
            {
                trigger.CommandText = "CREATE TRIGGER fail_replace_refresh BEFORE DELETE ON files BEGIN SELECT RAISE(ABORT, 'controlled refresh failure'); END;";
                await trigger.ExecuteNonQueryAsync();
            }

            var output = new StringWriter();
            Console.SetOut(output);
            Console.SetError(output);
            async Task Reindex(string relativePath)
            {
                var refreshed = await indexer.ReindexFilesAsync(root, [relativePath]);
                if (refreshed.SkippedFiles > 0) throw new InvalidOperationException(refreshed.SkippedFilePaths.First().Reason);
            }

            var exit = await ReplaceCommand.RunAsync(["replace", "notes.md", "--old", "old", "--new", "new"], root,
                reindexFile: Reindex, freshnessStore: store);
            Assert.Equal(1, exit);
            Assert.Equal("new", File.ReadAllText(file));
            Assert.Contains("replace applied, but index refresh failed", output.ToString(), StringComparison.OrdinalIgnoreCase);
            var search = await new ProtocolEngine(new SearchService(store, store), store, store).ExecuteAsync(new ProtocolRequest(
                "1.0", ProtocolOperations.SearchSymbol, session.Id.Value.ToString(), JsonSerializer.SerializeToElement(new { query = "Anything" })));
            Assert.False(search.Success);
            Assert.Contains(search.Errors, error => error.Code == "IndexDirty");
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldError);
            Directory.Delete(root, true);
            StateLock.Release();
        }
    }

    // A .md file has no build step and one passing "test", so the mode is visible in the output.
    private static VerifyAfterEdit FakeVerifier(string root) => new(
        _ => Task.FromResult(new ExecutionRecord("skip", true, "", [], "skipped (no build step for .md)")),
        _ => Task.FromResult(new ExecutionRecord("test", true, "", [], "PASS tests")),
        _ => []);

    private static async Task RunCase(string original, string[] options, int expected, string expectedContent, bool ambiguous = false, bool missing = false)
    {
        await StateLock.WaitAsync(); var oldDirectory = Environment.CurrentDirectory; var oldOut = Console.Out; var oldError = Console.Error;
        var root = Directory.CreateTempSubdirectory("devx-cli-replace").FullName; var file = Path.Combine(root, "sample.md"); File.WriteAllText(file, original);
        try
        {
            Environment.CurrentDirectory = root; var output = new StringWriter(); Console.SetOut(output); Console.SetError(new StringWriter());
            var entry = typeof(ArtifactCommand).Assembly.EntryPoint!;
            var call = entry.Invoke(null, [new[] { "replace", "sample.md" }.Concat(options).ToArray()]);
            var exit = call is Task<int> task ? await task : Convert.ToInt32(call);
            Assert.Equal(expected, exit); Assert.Equal(expectedContent, File.ReadAllText(file));
            if (ambiguous) Assert.Contains("2 times", output.ToString());
            if (missing) Assert.Equal(original, File.ReadAllText(file));
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); Environment.CurrentDirectory = oldDirectory; Directory.Delete(root, true); StateLock.Release(); }
    }
}
