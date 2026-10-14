using System.Text.Json;
using DevX.Infrastructure.Storage.SQLite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevX.Tests.Cli;

[Collection("ProcessGlobalState")]
public sealed class IndexCommandFixesTests : CliHarness
{
    [Fact]
    public async Task F1_IndexStatusAgentValueIsNotARoot()
    {
        var root = NewRoot("devx-oss51-f1-");
        await WithRoot(root, async directory =>
        {
            var (code, output) = await RunAsync(directory, "index", "status", "--agent", "claude", "--format", "json");
            Assert.Equal(0, code);
            using var json = JsonDocument.Parse(output);
            Assert.Equal(Real(directory), Real(json.RootElement.GetProperty("Data").GetProperty("RepositoryRoot").GetString()!));
        });
    }

    [Fact]
    public async Task F1_IndexUpdateAgentValueIsNotAPath()
    {
        var root = NewRoot("devx-oss51-f1u-");
        await WithRoot(root, async directory =>
        {
            var (_, sessionOutput) = await RunAsync(directory, "session", "create", "--format", "json");
            using var sessionJson = JsonDocument.Parse(sessionOutput);
            var id = sessionJson.RootElement.GetProperty("Data").GetProperty("sessionId").GetString()!;
            await File.WriteAllTextAsync(Path.Combine(directory, "A.py"), "class A: pass\n");
            var (code, output) = await RunAsync(directory, "index", "update", "A.py", "--session", id, "--agent", "claude", "--format", "json");
            Assert.Equal(0, code);
            Assert.DoesNotContain("claude", output, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task F1_IndexRootAgentValueIsNotAPath()
    {
        var root = NewRoot("devx-oss51-f1r-");
        await WithRoot(root, async directory =>
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "A.py"), "class A: pass\n");
            var (code, output) = await RunAsync(directory, "index", ".", "--agent", "claude", "--format", "json");
            Assert.Equal(0, code);
            Assert.Contains("Indexed 1 files", output, StringComparison.Ordinal);
            Assert.DoesNotContain("claude", output, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task F3_IndexChangedOutsideGitReturnsCleanError()
    {
        var root = NewRoot("devx-oss51-f3-");
        await WithRoot(root, async directory =>
        {
            var (_, sessionOutput) = await RunAsync(directory, "session", "create", "--format", "json");
            using var sessionJson = JsonDocument.Parse(sessionOutput);
            var id = sessionJson.RootElement.GetProperty("Data").GetProperty("sessionId").GetString()!;
            var (code, output) = await RunAsync(directory, "index", "changed", "--session", id, "--format", "json");
            Assert.Equal(1, code);
            using var error = JsonDocument.Parse(output);
            Assert.False(error.RootElement.GetProperty("Success").GetBoolean());
            Assert.Equal("index changed requires a git repository; use 'devx index <root>' instead.",
                error.RootElement.GetProperty("Data").GetProperty("Error").GetString());
        });
    }

    [Fact]
    public async Task F1_IndexChangedAgentValueIsNotARoot()
    {
        var root = NewRoot("devx-oss51-f1c-");
        await WithRoot(root, async directory =>
        {
            var id = await CreateSessionAsync(directory);
            var (code, output) = await RunAsync(directory, "index", "changed", "--session", id, "--agent", "claude", "--format", "json");
            // Outside git the command fails cleanly; the point is that "claude" was never treated as a path.
            Assert.Equal(1, code);
            Assert.DoesNotContain("claude", output, StringComparison.OrdinalIgnoreCase);
            using var error = JsonDocument.Parse(output);
            Assert.Contains("git repository", error.RootElement.GetProperty("Data").GetProperty("Error").GetString());
        });
    }

    [Fact]
    public async Task OSS53_IndexUpdateRemovesIndexedUntrackedDeletionAndUnfencesPatchValidation()
    {
        var root = NewRoot("devx-oss53-update-");
        await WithRoot(root, async directory =>
        {
            var id = await SeedAsync(directory);
            var patchPath = Path.Combine(directory, "patch.json");
            await File.WriteAllTextAsync(patchPath, JsonSerializer.Serialize(new
            {
                version = "1.0",
                operations = new[] { new { operation = "ReplaceText", path = "B.py", oldText = "class Beta: pass", replacementText = "class Beta:\n    value = 1", beforeState = "class Beta: pass\n" } }
            }));
            await MarkDirtyAsync(directory, "A.py");
            File.Delete(Path.Combine(directory, "A.py"));
            var (blockedCode, blockedOutput) = await RunAsync(directory, "patch", "validate", patchPath, "--session", id, "--format", "json");
            Assert.Equal(1, blockedCode);
            Assert.Contains("IndexDirty", blockedOutput, StringComparison.Ordinal);

            var (updateCode, updateOutput) = await RunAsync(directory, "index", "update", "A.py", "--session", id, "--format", "json");
            Assert.Equal(0, updateCode);
            using var update = JsonDocument.Parse(updateOutput);
            Assert.Equal(1, update.RootElement.GetProperty("RemovedFiles").GetInt32());
            Assert.Equal(0, update.RootElement.GetProperty("SkippedFiles").GetInt32());
            Assert.Null(await GetDirtyAsync(directory));
            var store = new SqliteStorageInitializer(ConnectionString(directory));
            Assert.Empty(await store.FindSymbolsByNameAsync("Alpha"));

            var (validateCode, validateOutput) = await RunAsync(directory, "patch", "validate", patchPath, "--session", id, "--format", "json");
            Assert.True(validateCode == 0, validateOutput);
            Assert.DoesNotContain("IndexDirty", validateOutput, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task OSS53_IndexRootRemovesTwoDeletedUntrackedRowsAndKeepsLiveSymbolsStable()
    {
        var root = NewRoot("devx-oss53-root-");
        await WithRoot(root, async directory =>
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "A.py"), "class Alpha: pass\n");
            await File.WriteAllTextAsync(Path.Combine(directory, "B.py"), "class Beta: pass\n");
            await File.WriteAllTextAsync(Path.Combine(directory, "live.py"), "class Live:\n    def keep(self):\n        return 1\n");
            InitializeGitRepo(directory);
            var (indexCode, _) = await RunAsync(directory, "index", ".");
            Assert.Equal(0, indexCode);
            var id = await CreateSessionAsync(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "temp-one.py"), "class TempOne: pass\n");
            await File.WriteAllTextAsync(Path.Combine(directory, "temp-two.py"), "class TempTwo: pass\n");
            var (updateCode, _) = await RunAsync(directory, "index", "update", "temp-one.py", "temp-two.py", "--session", id, "--format", "json");
            Assert.Equal(0, updateCode);
            File.Delete(Path.Combine(directory, "temp-one.py"));
            File.Delete(Path.Combine(directory, "temp-two.py"));

            var store = new SqliteStorageInitializer(ConnectionString(directory));
            var lineBefore = Assert.Single(await store.FindMethodSourcesAsync("keep")).StartLine;
            var (code, output) = await RunAsync(directory, "index", ".");
            Assert.Equal(0, code);
            Assert.Contains("removed 2", output, StringComparison.OrdinalIgnoreCase);
            var paths = await store.GetIndexedFilePathsAsync(Real(directory));
            Assert.Contains("live.py", paths);
            Assert.DoesNotContain("temp-one.py", paths);
            Assert.DoesNotContain("temp-two.py", paths);
            var lineAfter = Assert.Single(await store.FindMethodSourcesAsync("keep")).StartLine;
            Assert.Equal(lineBefore, lineAfter);
        });
    }

    [Fact]
    public async Task F2_IndexUpdateOnMissingFileRemovesItAndClearsDirty()
    {
        var root = NewRoot("devx-oss51-f2a-");
        await WithRoot(root, async directory =>
        {
            var id = await SeedAsync(directory);
            await MarkDirtyAsync(directory, "A.py");
            Assert.NotNull(await GetDirtyAsync(directory));
            File.Delete(Path.Combine(directory, "A.py"));

            var (code, output) = await RunAsync(directory, "index", "update", "A.py", "--session", id, "--format", "json");
            Assert.Equal(0, code);
            using var update = JsonDocument.Parse(output);
            Assert.Equal(0, update.RootElement.GetProperty("SkippedFiles").GetInt32());

            Assert.Null(await GetDirtyAsync(directory));
            Assert.Equal(0, await CountDirtyRowsAsync(directory));
            var (_, alpha) = await RunAsync(directory, "search", "symbol", "Alpha", "--session", id, "--format", "json");
            Assert.DoesNotContain("Alpha", alpha, StringComparison.Ordinal);
            var (_, beta) = await RunAsync(directory, "search", "symbol", "Beta", "--session", id, "--format", "json");
            Assert.Contains("Beta", beta, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task F2_IndexChangedReconcilesMissingDirtyFiles()
    {
        var root = NewRoot("devx-oss51-f2b-");
        await WithRoot(root, async directory =>
        {
            var id = await SeedAsync(directory);
            await MarkDirtyAsync(directory, "A.py");
            File.Delete(Path.Combine(directory, "A.py"));

            var (code, _) = await RunAsync(directory, "index", "changed", "--session", id, "--format", "json");
            // No git repository here: the clean error is expected, but the reconcile runs first.
            Assert.Equal(1, code);
            Assert.Null(await GetDirtyAsync(directory));
            Assert.Equal(0, await CountDirtyRowsAsync(directory));
            var (_, alpha) = await RunAsync(directory, "search", "symbol", "Alpha", "--session", id, "--format", "json");
            Assert.DoesNotContain("Alpha", alpha, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task F2_IndexStatusReportsDirtyFiles()
    {
        var root = NewRoot("devx-oss51-f2c-");
        await WithRoot(root, async directory =>
        {
            await SeedAsync(directory);
            var (_, clean) = await RunAsync(directory, "index", "status", "--format", "json");
            using (var cleanJson = JsonDocument.Parse(clean))
                Assert.Equal("Current", cleanJson.RootElement.GetProperty("Data").GetProperty("Status").GetString());

            await MarkDirtyAsync(directory, "A.py");
            var (code, output) = await RunAsync(directory, "index", "status", "--format", "json");
            Assert.Equal(0, code);
            using var json = JsonDocument.Parse(output);
            var data = json.RootElement.GetProperty("Data");
            Assert.Equal("Stale", data.GetProperty("Status").GetString());
            Assert.Equal("1 dirty file(s) remain unreconciled.", data.GetProperty("Reason").GetString());
        });
    }

    [Fact]
    public async Task F2_PatchValidateWorksAfterDeletionReconciled()
    {
        var root = NewRoot("devx-oss51-f2d-");
        await WithRoot(root, async directory =>
        {
            var id = await SeedAsync(directory);
            var patchPath = Path.Combine(directory, "patch.json");
            await File.WriteAllTextAsync(patchPath, JsonSerializer.Serialize(new
            {
                version = "1.0",
                operations = new[] { new { operation = "ReplaceText", path = "B.py", oldText = "class Beta: pass", replacementText = "class Beta:\n    x = 1", beforeState = "class Beta: pass\n" } }
            }));
            await MarkDirtyAsync(directory, "A.py");
            File.Delete(Path.Combine(directory, "A.py"));

            var (blockedCode, blocked) = await RunAsync(directory, "patch", "validate", patchPath, "--session", id, "--format", "json");
            Assert.Equal(1, blockedCode);
            Assert.Contains("IndexDirty", blocked, StringComparison.Ordinal);

            var (updateCode, _) = await RunAsync(directory, "index", "update", "A.py", "--session", id, "--format", "json");
            Assert.Equal(0, updateCode);
            var (code, output) = await RunAsync(directory, "patch", "validate", patchPath, "--session", id, "--format", "json");
            Assert.True(code == 0, output);
            Assert.DoesNotContain("IndexDirty", output, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task F4_SessionCreateOnReadOnlyDbSucceedsWithWarning()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = NewRoot("devx-oss51-f4a-");
        try
        {
            var seeded = await SeedAsync(root);
            using var lockHandle = LockReadOnly(root);
            var (code, output) = await RunAsync(root, "session", "create", "--format", "json");
            Assert.Equal(0, code);
            using var json = JsonDocument.Parse(output);
            Assert.True(json.RootElement.GetProperty("Success").GetBoolean());
            Assert.True(Guid.TryParse(json.RootElement.GetProperty("Data").GetProperty("sessionId").GetString(), out _));
            var diagnostic = Assert.Single(json.RootElement.GetProperty("Diagnostics").EnumerateArray());
            Assert.Equal("HistoryNotRecorded", diagnostic.GetProperty("Code").GetString());
            Assert.DoesNotContain("Unhandled", output, StringComparison.Ordinal);
            Assert.NotEmpty(seeded);
        }
        finally
        {
            RestoreWritable(root);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task F4_SessionlessReadOnReadOnlyDbSucceeds()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = NewRoot("devx-oss51-f4b-");
        try
        {
            await SeedAsync(root);
            using var lockHandle = LockReadOnly(root);
            var (code, output) = await RunAsync(root, "read", "method", "Alpha.method");
            Assert.True(code == 0, output);
            Assert.Contains("def method", output, StringComparison.Ordinal);
            Assert.DoesNotContain("Unhandled", output, StringComparison.Ordinal);
            Assert.Contains("warning: HistoryNotRecorded", output, StringComparison.Ordinal);
        }
        finally
        {
            RestoreWritable(root);
            Directory.Delete(root, recursive: true);
        }
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static IDisposable LockReadOnly(string directory)
    {
        var state = Path.Combine(directory, ".devx");
        File.SetUnixFileMode(Path.Combine(state, "devx.db"), UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        File.SetUnixFileMode(state, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return new Unlock(directory);
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private sealed class Unlock(string directory) : IDisposable
    {
        public void Dispose() => RestoreWritable(directory);
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void RestoreWritable(string directory)
    {
        var state = Path.Combine(directory, ".devx");
        if (Directory.Exists(state)) File.SetUnixFileMode(state, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var db = Path.Combine(state, "devx.db");
        if (File.Exists(db)) File.SetUnixFileMode(db, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    // The OS resolves symlinks (macOS /var -> /private/var) when it reports the working directory,
    // and that is the form the CLI stores, so compare through the same lens.
    private static string Real(string path)
    {
        var original = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = path;
            return Environment.CurrentDirectory;
        }
        finally
        {
            Environment.CurrentDirectory = original;
        }
    }

    private static async Task<string> CreateSessionAsync(string directory)
    {
        var (_, sessionOutput) = await RunAsync(directory, "session", "create", "--format", "json");
        using var sessionJson = JsonDocument.Parse(sessionOutput);
        return sessionJson.RootElement.GetProperty("Data").GetProperty("sessionId").GetString()!;
    }

    private static void InitializeGitRepo(string directory)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("init");
        startInfo.ArgumentList.Add("-q");
        using var process = System.Diagnostics.Process.Start(startInfo)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private static async Task<string> SeedAsync(string directory)
    {
        await File.WriteAllTextAsync(Path.Combine(directory, "A.py"), "class Alpha:\n    def method(self): pass\n");
        await File.WriteAllTextAsync(Path.Combine(directory, "B.py"), "class Beta: pass\n");
        var (code, output) = await RunAsync(directory, "index", ".");
        Assert.True(code == 0, output);
        return await CreateSessionAsync(directory);
    }

    private static string ConnectionString(string directory)
        => new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, ".devx", "devx.db") }.ToString();

    private static async Task MarkDirtyAsync(string directory, string file)
    {
        var store = new SqliteStorageInitializer(ConnectionString(directory));
        await store.InitializeAsync();
        await store.MarkIndexFilesDirtyAsync(Real(directory), [file]);
    }

    private static async Task<DevX.Domain.DirtyIndexRepository?> GetDirtyAsync(string directory)
    {
        var store = new SqliteStorageInitializer(ConnectionString(directory));
        await store.InitializeAsync();
        return await store.GetDirtyIndexRepositoryAsync(Real(directory));
    }

    private static async Task<long> CountDirtyRowsAsync(string directory)
    {
        await using var database = new SqliteConnection(ConnectionString(directory));
        await database.OpenAsync();
        long total = 0;
        foreach (var table in new[] { "dirty_index_files", "dirty_index_repositories" })
        {
            await using var command = database.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table};";
            try { total += (long)(await command.ExecuteScalarAsync())!; }
            catch (SqliteException) { /* table absent in this schema */ }
        }
        return total;
    }
}
