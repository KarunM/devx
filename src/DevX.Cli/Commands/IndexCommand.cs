using System.Text.Json;
using DevX.Application;
using DevX.Domain;
using DevX.Cli;
using DevX.Infrastructure.Storage.SQLite;
using DevX.Indexer;
using DevX.Protocol;
using Microsoft.Data.Sqlite;
using IOFile = System.IO.File;

static partial class Program
{
    static async Task IndexAsync(string connectionString, string repositoryPath, string[] args, string? agentLabel)
    {
        var started = DateTimeOffset.UtcNow;
        var store = new SqliteStorageInitializer(connectionString);
        await store.InitializeAsync();
        var indexer = new RepositoryIndexingService(store);

        // `devx index .` on an untouched checkout used to walk and re-parse every file: on Django
        // that is 3,292 files and over 13 minutes of work to arrive at the index that was already
        // on disk. And on a checkout with one edited file it did the same 13 minutes, because the
        // CLI had no notion of a changed file at all. The MCP server got both answers; the CLI --
        // the way the benchmark drives it -- did not, which is the call that looked like a hang.
        if (!args.Contains("--force", StringComparer.Ordinal) && await CurrentIndexAsync(indexer, repositoryPath) is { } answer)
        {
            await store.AddOperationMetricAsync(new OperationMetric("index", started, DateTimeOffset.UtcNow - started, true, null, AgentLabel: agentLabel));
            Console.WriteLine(answer);
            return;
        }

        var result = await indexer.IndexAsync(repositoryPath);
        await store.AddOperationMetricAsync(new OperationMetric("index", started, DateTimeOffset.UtcNow - started, true, null, AgentLabel: agentLabel));
        Console.WriteLine(result.Reason is null
            ? $"Indexed {result.FilesIndexed} files."
            : $"Indexed {result.FilesIndexed} files. Reason: {result.Reason}.");
    }

    /// <summary>
    /// What `devx index .` should say instead of doing the full run, or null when the full run is
    /// genuinely needed. Git being unavailable (a plain folder, no repository) means DevX cannot
    /// tell what changed, and the answer to "cannot tell" is to do the work.
    /// </summary>
    static async Task<string?> CurrentIndexAsync(RepositoryIndexingService indexer, string repositoryPath)
    {
        IReadOnlyList<string> changed;
        try
        {
            changed = await indexer.GetChangedFilesAsync(repositoryPath);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return null;
        }

        var indexed = await indexer.CountIndexedFilesAsync(repositoryPath);

        // Nothing is indexed yet, so there is nothing to bring up to date file by file.
        if (indexed == 0) return null;
        if (changed.Count == 0) return $"already current ({indexed} files).";

        // A repository that is already indexed and has a handful of edited files is the common
        // case an agent is in, and re-parsing its whole tree for those files is the expensive
        // mistake. Only the files git names are re-indexed.
        var refreshed = await indexer.ReindexChangedFilesAsync(repositoryPath);
        return $"Indexed {refreshed.UpdatedFiles} files (re-indexed the {changed.Count} files git reports as changed, out of {indexed}).";
    }

    static async Task<int> IndexUpdateAsync(string connectionString, string[] args, string format)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: devx index update <file> [<file> ...] --session <id>");
            return 1;
        }

        var sessionId = GetOption(args, "--session");
        if (sessionId is null)
        {
            Console.Error.WriteLine("Missing --session.");
            return 1;
        }

        // Drop every flag AND the value that follows it. The previous predicate compared each arg to
        // its predecessor within the same skipped slice but still admitted a flag value whenever the
        // flag sat at the slice boundary, so `--session <guid>` leaked the GUID through as a file path
        // and every run printed a bogus "SKIPPED: <guid> — Unsupported file type ''".
        var positional = args.Skip(2).ToArray();
        var filePaths = new List<string>();
        for (var i = 0; i < positional.Length; i++)
        {
            if (positional[i].StartsWith("--", StringComparison.Ordinal))
            {
                // Flags that take a value consume the next token; boolean flags do not.
                if (i + 1 < positional.Length && !positional[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    i++;
                }

                continue;
            }

            filePaths.Add(positional[i]);
        }

        if (filePaths.Count == 0)
        {
            Console.Error.WriteLine("Missing file path.");
            return 1;
        }

        var store = new SqliteStorageInitializer(connectionString);
        await store.InitializeAsync();
        var session = await store.GetSessionAsync(new GetSessionRequest(new SessionId(Guid.Parse(sessionId))));
        if (session is null)
        {
            Console.Error.WriteLine("Session not found.");
            return 1;
        }

        var root = await store.GetRepositoryRootPathAsync(session.RepositoryId);
        var repositoryRoot = root ?? Environment.CurrentDirectory;
        var indexer = new RepositoryIndexingService(store);
        var started = DateTimeOffset.UtcNow;
        var result = await indexer.ReindexFilesAsync(repositoryRoot, filePaths.ToArray());
        var errorSkips = result.SkippedFilePaths.Where(file => file.Reason.StartsWith("ERROR:", StringComparison.Ordinal)).ToArray();
        var hasNonMissingError = errorSkips.Any(file => file.Reason != "ERROR: file missing");
        // Preserve partial success when at least one requested file refreshed and another is
        // missing; the missing path remains fenced, so this does not claim the whole index is fresh.
        var updateSucceeded = !hasNonMissingError && (result.UpdatedFiles > 0 || errorSkips.Length == 0);
        await store.AddOperationMetricAsync(new OperationMetric("index update", started, DateTimeOffset.UtcNow - started, updateSucceeded, sessionId,
            JsonSerializer.Serialize(new { UnsupportedFilesSkipped = result.SkippedFilePaths.Count(f => f.Reason.StartsWith("Unsupported file type", StringComparison.Ordinal)) }), GetAgentLabel(args)));

        if (format == "json")
        {
            Console.WriteLine(JsonSerializer.Serialize(new { Success = updateSucceeded, result.Mode, result.UpdatedFiles, result.SkippedFiles, result.RequestedFiles, result.PerformedFullReindex, result.SkippedFilePaths }));
        }
        else
        {
            Console.WriteLine(result.PerformedFullReindex
                ? "Performed full repository re-index due to structural file changes."
                : $"Updated file index. {result.UpdatedFiles} updated, {result.SkippedFiles} skipped.");

            foreach (var skipped in result.SkippedFilePaths)
            {
                Console.WriteLine($"  SKIPPED: {skipped.Path} — {skipped.Reason}");
            }
        }

        return updateSucceeded ? 0 : 1;
    }

    static async Task<int> IndexChangedAsync(string connectionString, string[] args, string format)
    {
        var sessionId = GetOption(args, "--session");
        if (sessionId is null)
        {
            Console.Error.WriteLine("Missing --session.");
            return 1;
        }

        var store = new SqliteStorageInitializer(connectionString);
        await store.InitializeAsync();
        var session = await store.GetSessionAsync(new GetSessionRequest(new SessionId(Guid.Parse(sessionId))));
        if (session is null)
        {
            Console.Error.WriteLine("Session not found.");
            return 1;
        }

        var root = await store.GetRepositoryRootPathAsync(session.RepositoryId);
        var repositoryRoot = root ?? Environment.CurrentDirectory;
        var indexer = new RepositoryIndexingService(store);
        var started = DateTimeOffset.UtcNow;
        var result = await indexer.ReindexChangedFilesAsync(repositoryRoot);
        await store.AddOperationMetricAsync(new OperationMetric("index changed", started, DateTimeOffset.UtcNow - started, true, sessionId, AgentLabel: GetAgentLabel(args)));

        if (format == "json")
        {
            Console.WriteLine(JsonSerializer.Serialize(new { Success = true, result.Mode, result.UpdatedFiles, result.SkippedFiles, result.RequestedFiles, result.PerformedFullReindex }));
        }
        else
        {
            Console.WriteLine(result.RequestedFiles.Count == 0
                ? "No changed files required re-indexing."
                : result.PerformedFullReindex
                    ? "Changed structural files detected; performed full repository re-index."
                    : "Updated changed file indexes.");
        }

        return 0;
    }

    static async Task<int> IndexStatusAsync(string connectionString, string[] args, string format)
    {
        var sessionId = GetOption(args, "--session");
        string? repositoryPath = null;

        for (var index = 2; index < args.Length; index++)
        {
            var arg = args[index];
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (arg == "--session" || arg == "--format")
                {
                    index++;
                }
                continue;
            }

            if (arg.Equals("json", StringComparison.OrdinalIgnoreCase) || arg.Equals("text", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            repositoryPath = arg;
            break;
        }

        var started = DateTimeOffset.UtcNow;
        var store = new SqliteStorageInitializer(connectionString);
        await store.InitializeAsync();

        if (string.IsNullOrWhiteSpace(repositoryPath))
        {
            repositoryPath = Path.GetFullPath(Environment.CurrentDirectory);
        }

        var repositoryRoot = Path.GetFullPath(repositoryPath);
        var repository = await store.GetRepositoryByRootPathAsync(repositoryRoot);
        var indexedFiles = repository is null ? 0 : await store.CountIndexedFilesAsync(repository.Id);
        var response = new IndexStatusResponse(
            RepositoryIndexed: repository is not null,
            RepositoryRoot: repositoryRoot,
            IndexedFiles: indexedFiles,
            IndexExists: repository is not null,
            Status: repository is null ? "Missing" : "Current",
            Reason: repository is null ? "Repository has not been indexed yet." : "No obvious stale-index signal detected.");

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            await store.AddOperationMetricAsync(new OperationMetric("index status", started, DateTimeOffset.UtcNow - started, true, sessionId, AgentLabel: GetAgentLabel(args)));
        }

        WriteResponse(new ProtocolResponse(true, JsonSerializer.SerializeToElement(response), [], []), format);
        return 0;
    }

    static async Task EnsureAutomaticIndexAsync()
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = ResolveDatabasePath() }.ToString();
        var store = new SqliteStorageInitializer(connectionString);
        await store.InitializeAsync();
        var root = Path.GetFullPath(Environment.CurrentDirectory);
        for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".devx")) || Directory.Exists(Path.Combine(directory.FullName, ".git")) || IOFile.Exists(Path.Combine(directory.FullName, ".git")))
            {
                root = directory.FullName;
                break;
            }
        }
        var repository = await store.GetRepositoryByRootPathAsync(root);
        if (repository is not null && await store.CountIndexedFilesAsync(repository.Id) > 0)
            return;
        var result = await new RepositoryIndexingService(store).IndexAsync(root);
        Console.Error.WriteLine($"(indexed {result.FilesIndexed} files)");
    }
}
