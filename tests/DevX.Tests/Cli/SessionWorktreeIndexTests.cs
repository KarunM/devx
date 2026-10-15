using System.Diagnostics;
using System.Text.Json;
using DevX.Infrastructure.Storage.SQLite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevX.Tests.Cli;

/// <summary>Regression tests for sessions across git worktrees and for index reconciliation after pull/merge/commit.</summary>
[Collection("ProcessGlobalState")]
public sealed class SessionWorktreeIndexTests : CliHarness
{
    private const string Unknown = "00000000-0000-0000-0000-000000000000";

    [Fact]
    public async Task A1_SessionCreatedInMainCheckoutResolvesInAWorktree_AndIndexesTheWorktreeItself()
    {
        var root = NewRoot("devx-a1-");
        var worktree = root + "-wt";
        try
        {
            Git(root, "init", "-q");
            await File.WriteAllTextAsync(Path.Combine(root, "A.py"), "class Alpha: pass\n");
            Commit(root, "A.py");
            var id = await CreateSessionAsync(root);
            Git(root, "worktree", "add", "-q", worktree, "-b", "wt-branch");
            await File.WriteAllTextAsync(Path.Combine(worktree, "G.py"), "class OnlyInWorktree: pass\n");

            var (indexCode, indexOutput) = await RunAsync(worktree, "index", ".", "--session", id);
            Assert.True(indexCode == 0, indexOutput);
            var (code, output) = await RunAsync(worktree, "search", "symbol", "OnlyInWorktree", "--session", id, "--format", "json");
            Assert.True(code == 0, output);
            Assert.Contains("OnlyInWorktree", output, StringComparison.Ordinal);

            var (_, status) = await RunAsync(worktree, "index", "status", "--session", id, "--format", "json");
            using var json = JsonDocument.Parse(status);
            Assert.Equal(Real(worktree), json.RootElement.GetProperty("Data").GetProperty("RepositoryRoot").GetString());
            Assert.True(json.RootElement.GetProperty("Data").GetProperty("IndexedFiles").GetInt32() >= 2);

            // The worktree-only file never leaked into the main checkout's index.
            var (_, mainSearch) = await RunAsync(root, "search", "symbol", "OnlyInWorktree", "--session", id, "--format", "json");
            Assert.DoesNotContain("\"OnlyInWorktree\"", mainSearch, StringComparison.Ordinal);
        }
        finally
        {
            Git(root, "worktree", "remove", "--force", worktree);
            Directory.Delete(root, recursive: true);
            if (Directory.Exists(worktree)) Directory.Delete(worktree, recursive: true);
        }
    }

    [Fact]
    public async Task A1_SessionFromAnUnrelatedRepositoryDoesNotResolve()
    {
        var one = NewRoot("devx-a1-one-");
        var two = NewRoot("devx-a1-two-");
        try
        {
            Git(one, "init", "-q");
            Git(two, "init", "-q");
            var id = await CreateSessionAsync(one);
            var (code, output) = await RunAsync(two, "index", "status", "--session", id);
            Assert.Equal(1, code);
            Assert.Contains($"Session '{id}' was not found.", output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(one, recursive: true);
            Directory.Delete(two, recursive: true);
        }
    }

    [Theory]
    [InlineData("index", ".")]
    [InlineData("index", "update", "A.py")]
    [InlineData("index", "changed")]
    [InlineData("index", "status")]
    [InlineData("search", "symbol", "Alpha")]
    public async Task A2_UnknownSessionIsRejectedByEveryCommand(params string[] command)
    {
        var root = NewRoot("devx-a2-");
        await WithRoot(root, async directory =>
        {
            Git(directory, "init", "-q");
            await File.WriteAllTextAsync(Path.Combine(directory, "A.py"), "class Alpha: pass\n");
            var (code, output) = await RunAsync(directory, [.. command, "--session", Unknown]);
            Assert.Equal(1, code);
            Assert.Contains($"Session '{Unknown}' was not found.", output, StringComparison.Ordinal);
            Assert.DoesNotContain("already current", output, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A3_FilesArrivingByMergeAreIndexedAndDeletedOnesRemoved()
    {
        var root = NewRoot("devx-a3-");
        await WithRoot(root, async directory =>
        {
            Git(directory, "init", "-q", "-b", "main");
            await File.WriteAllTextAsync(Path.Combine(directory, "A.py"), "class Alpha: pass\n");
            await File.WriteAllTextAsync(Path.Combine(directory, "D.py"), "class Delta: pass\n");
            Commit(directory, "A.py", "D.py");
            var (indexCode, indexOutput) = await RunAsync(directory, "index", ".");
            Assert.True(indexCode == 0, indexOutput);
            var id = await CreateSessionAsync(directory);

            Git(directory, "checkout", "-q", "-b", "feat");
            await File.WriteAllTextAsync(Path.Combine(directory, "A.py"), "class AlphaRenamed: pass\n");
            await File.WriteAllTextAsync(Path.Combine(directory, "B.py"), "class Beta: pass\n");
            File.Delete(Path.Combine(directory, "D.py"));
            Git(directory, "rm", "-q", "--cached", "D.py");
            Commit(directory, "A.py", "B.py");
            Git(directory, "checkout", "-q", "main");
            Git(directory, "merge", "-q", "--ff-only", "feat");
            Assert.Empty(Git(directory, "status", "--porcelain").Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(line => !line.Contains(".devx/")));

            var (code, output) = await RunAsync(directory, "index", ".", "--session", id);
            Assert.True(code == 0, output);
            Assert.DoesNotContain("already current", output, StringComparison.Ordinal);

            var (_, beta) = await RunAsync(directory, "search", "symbol", "Beta", "--session", id, "--format", "json");
            Assert.Contains("\"Beta\"", beta, StringComparison.Ordinal);
            var (_, renamed) = await RunAsync(directory, "search", "symbol", "AlphaRenamed", "--session", id, "--format", "json");
            Assert.Contains("\"AlphaRenamed\"", renamed, StringComparison.Ordinal);
            var (_, oldName) = await RunAsync(directory, "search", "symbol", "Alpha", "--session", id, "--format", "json");
            Assert.DoesNotContain("\"Alpha\"", oldName, StringComparison.Ordinal);
            var (_, delta) = await RunAsync(directory, "search", "symbol", "Delta", "--session", id, "--format", "json");
            Assert.DoesNotContain("\"Delta\"", delta, StringComparison.Ordinal);

            // Nothing changed since: the next run really is current.
            var (_, again) = await RunAsync(directory, "index", ".", "--session", id);
            Assert.Contains("already current", again, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A3_IndexChangedAlsoPicksUpFilesFromAMerge()
    {
        var root = NewRoot("devx-a3c-");
        await WithRoot(root, async directory =>
        {
            Git(directory, "init", "-q", "-b", "main");
            await File.WriteAllTextAsync(Path.Combine(directory, "A.py"), "class Alpha: pass\n");
            Commit(directory, "A.py");
            var (_, _) = await RunAsync(directory, "index", ".");
            var id = await CreateSessionAsync(directory);
            Git(directory, "checkout", "-q", "-b", "feat");
            await File.WriteAllTextAsync(Path.Combine(directory, "B.py"), "class Beta: pass\n");
            Commit(directory, "B.py");
            Git(directory, "checkout", "-q", "main");
            Git(directory, "merge", "-q", "--ff-only", "feat");

            var (code, output) = await RunAsync(directory, "index", "changed", "--session", id);
            Assert.True(code == 0, output);
            var (_, beta) = await RunAsync(directory, "search", "symbol", "Beta", "--session", id, "--format", "json");
            Assert.Contains("\"Beta\"", beta, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A4_CommittedAndReindexedFilesClearTheDirtyStateAndStatusNamesThem()
    {
        var root = NewRoot("devx-a4-");
        await WithRoot(root, async directory =>
        {
            Git(directory, "init", "-q");
            await File.WriteAllTextAsync(Path.Combine(directory, "A.py"), "class Alpha: pass\n");
            await File.WriteAllTextAsync(Path.Combine(directory, "B.py"), "class Beta: pass\n");
            Commit(directory, "A.py", "B.py");
            await RunAsync(directory, "index", ".");
            var id = await CreateSessionAsync(directory);

            await MarkAsync(directory, full: true, "A.py", "B.py");
            var (_, stale) = await RunAsync(directory, "index", "status", "--session", id, "--format", "json");
            using (var staleJson = JsonDocument.Parse(stale))
            {
                var data = staleJson.RootElement.GetProperty("Data");
                Assert.Equal("Stale", data.GetProperty("Status").GetString());
                Assert.Equal(["A.py", "B.py"], data.GetProperty("DirtyFiles").EnumerateArray().Select(item => item.GetString()!).ToArray());
                Assert.Contains("A.py, B.py", data.GetProperty("Reason").GetString(), StringComparison.Ordinal);
            }

            File.AppendAllText(Path.Combine(directory, "A.py"), "class Gamma: pass\n");
            Commit(directory, "A.py");
            var (updateCode, updateOutput) = await RunAsync(directory, "index", "update", "A.py", "B.py", "--session", id);
            Assert.Equal(0, updateCode);
            var (changedCode, _) = await RunAsync(directory, "index", "changed", "--session", id);
            Assert.Equal(0, changedCode);
            var (dotCode, dotOutput) = await RunAsync(directory, "index", ".", "--session", id);
            Assert.Equal(0, dotCode);

            var (_, current) = await RunAsync(directory, "index", "status", "--session", id, "--format", "json");
            using var currentJson = JsonDocument.Parse(current);
            Assert.Equal("Current", currentJson.RootElement.GetProperty("Data").GetProperty("Status").GetString());
            Assert.False(currentJson.RootElement.GetProperty("Data").TryGetProperty("DirtyFiles", out _));
            var (_, gamma) = await RunAsync(directory, "search", "symbol", "Gamma", "--session", id, "--format", "json");
            Assert.Contains("\"Gamma\"", gamma, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A4_FileLevelDirtyEntriesCommittedSinceAreClearedByIndexChanged()
    {
        var root = NewRoot("devx-a4b-");
        await WithRoot(root, async directory =>
        {
            Git(directory, "init", "-q");
            await File.WriteAllTextAsync(Path.Combine(directory, "A.py"), "class Alpha: pass\n");
            await File.WriteAllTextAsync(Path.Combine(directory, "notes.txt"), "plain\n");
            Commit(directory, "A.py", "notes.txt");
            await RunAsync(directory, "index", ".");
            var id = await CreateSessionAsync(directory);
            await MarkAsync(directory, full: false, "A.py", "notes.txt");
            File.AppendAllText(Path.Combine(directory, "A.py"), "class Gamma: pass\n");
            Commit(directory, "A.py");

            var (code, output) = await RunAsync(directory, "index", "changed", "--session", id);
            Assert.True(code == 0, output);
            var (_, status) = await RunAsync(directory, "index", "status", "--session", id, "--format", "json");
            using var json = JsonDocument.Parse(status);
            Assert.Equal("Current", json.RootElement.GetProperty("Data").GetProperty("Status").GetString());
            var (_, gamma) = await RunAsync(directory, "search", "symbol", "Gamma", "--session", id, "--format", "json");
            Assert.Contains("\"Gamma\"", gamma, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A4_CleanRepositoryStillReportsAlreadyCurrent()
    {
        var root = NewRoot("devx-a4c-");
        await WithRoot(root, async directory =>
        {
            Git(directory, "init", "-q");
            await File.WriteAllTextAsync(Path.Combine(directory, "A.py"), "class Alpha: pass\n");
            Commit(directory, "A.py");
            await RunAsync(directory, "index", ".");
            var (code, output) = await RunAsync(directory, "index", ".");
            Assert.Equal(0, code);
            Assert.Contains("already current (1 files).", output, StringComparison.Ordinal);
        });
    }

    private static async Task MarkAsync(string directory, bool full, params string[] files)
    {
        var store = new SqliteStorageInitializer(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, ".devx", "devx.db") }.ToString());
        await store.InitializeAsync();
        if (full) await store.MarkIndexRepositoryDirtyAsync(Real(directory), files);
        else await store.MarkIndexFilesDirtyAsync(Real(directory), files);
    }

    private static async Task<string> CreateSessionAsync(string directory)
    {
        var (_, output) = await RunAsync(directory, "session", "create", "--format", "json");
        using var json = JsonDocument.Parse(output);
        return json.RootElement.GetProperty("Data").GetProperty("sessionId").GetString()!;
    }

    private static string Real(string path)
    {
        var original = Environment.CurrentDirectory;
        try { Environment.CurrentDirectory = path; return Environment.CurrentDirectory; }
        finally { Environment.CurrentDirectory = original; }
    }

    private static void Commit(string directory, params string[] files)
    {
        Git(directory, ["add", .. files]);
        Git(directory, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "c");
    }

    private static string Git(string directory, params string[] args)
    {
        var startInfo = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) startInfo.ArgumentList.Add(arg);
        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return output;
    }
}
