using System.Text.Json;
using System.Security.Cryptography;
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
    static async Task<int> SessionAsync(string connectionString, string[] args, string format)
    {
        if (args.Length < 2 || args[1] != "create")
        {
            Console.Error.WriteLine("Usage: devx session create");
            return 1;
        }

        var store = new SqliteStorageInitializer(connectionString);
        await store.InitializeAsync();
        var repositoryRoot = Path.GetFullPath(Environment.CurrentDirectory);
        var repository = new Repository(new RepositoryId(StableId(repositoryRoot)), Path.GetFileName(repositoryRoot), repositoryRoot);
        await store.InitializeAsync();
        await store.UpsertRepositoryAsync(repository);
        var started = DateTimeOffset.UtcNow;
        var session = await store.CreateSessionAsync(new CreateSessionRequest(repository.Id));
        await store.AddOperationMetricAsync(new OperationMetric("session create", started, DateTimeOffset.UtcNow - started, true, session.Id.Value.ToString(), AgentLabel: GetAgentLabel(args)));
        WriteResponse(new ProtocolResponse(true, JsonSerializer.SerializeToElement(new { sessionId = session.Id.Value.ToString() }), [], []), format);
        return 0;
    }

    static Guid StableId(string value)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(guidBytes);
        return new Guid(guidBytes);
    }

    static async Task<string[]> AddAutomaticSessionAsync(string[] args)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = ResolveDatabasePath() }.ToString();
        var store = new SqliteStorageInitializer(connectionString);
        await store.InitializeAsync();
        var root = FindRepositoryRoot(Environment.CurrentDirectory);
        var repository = new Repository(new RepositoryId(StableId(root)), Path.GetFileName(root), root);
        var session = await AutomaticSessionResolver.GetOrCreateAsync(store, root, repository);
        return [.. args, "--session", session.Id.Value.ToString()];
    }

    static string FindRepositoryRoot(string path)
    {
        var root = Path.GetFullPath(path);
        for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".devx")) || Directory.Exists(Path.Combine(directory.FullName, ".git")) || IOFile.Exists(Path.Combine(directory.FullName, ".git")))
                return directory.FullName;
        }
        return root;
    }

    static async Task<(string RepositoryRoot, int IndexedFiles, bool RepositoryIndexed, string? SessionId)> ResolveRuntimeReadinessInputsAsync(
        SqliteStorageInitializer store,
        string[] args)
    {
        var sessionId = GetOption(args, "--session");
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            var session = await store.GetSessionAsync(new GetSessionRequest(new SessionId(Guid.Parse(sessionId))));
            if (session is not null)
            {
                var repositoryRootFromSession = await store.GetRepositoryRootPathAsync(session.RepositoryId);
                if (!string.IsNullOrWhiteSpace(repositoryRootFromSession))
                {
                    return (
                        Path.GetFullPath(repositoryRootFromSession),
                        await store.CountIndexedFilesAsync(session.RepositoryId),
                        true,
                        sessionId);
                }
            }
        }

        var repositoryRoot = Path.GetFullPath(Environment.CurrentDirectory);
        var repository = await store.GetRepositoryByRootPathAsync(repositoryRoot);
        return (
            repositoryRoot,
            repository is null ? 0 : await store.CountIndexedFilesAsync(repository.Id),
            repository is not null,
            sessionId);
    }

    static async Task<(SessionState? Session, string? ErrorMessage, bool Reused, bool Created)> ResolveSessionAsync(string connectionString, string[] args, bool allowCreate, bool allowReuse)
    {
        var explicitSessionId = GetOption(args, "--session");
        var store = new SqliteStorageInitializer(connectionString);
        await store.InitializeAsync();

        if (!string.IsNullOrWhiteSpace(explicitSessionId))
        {
            var explicitSession = await store.GetSessionAsync(new GetSessionRequest(new SessionId(Guid.Parse(explicitSessionId))));
            return explicitSession is null
                ? (null, "Session not found.", false, false)
                : (explicitSession, null, false, false);
        }

        var repositoryRoot = Path.GetFullPath(Environment.CurrentDirectory);
        var repository = await store.GetRepositoryByRootPathAsync(repositoryRoot);
        if (repository is null)
        {
            return (null, "Repository has not been indexed yet.", false, false);
        }

        if (allowReuse)
        {
            var activeSession = await GetActiveSessionAsync(store, repository.Id);
            if (activeSession is not null)
            {
                Console.WriteLine($"Auto-reused active session: {activeSession.Id.Value}");
                return (activeSession, null, true, false);
            }
        }

        if (!allowCreate)
        {
            return (null, "No active session available.", false, false);
        }

        var created = await store.CreateSessionAsync(new CreateSessionRequest(repository.Id));
        await SetActiveSessionAsync(store, repository.Id, created.Id.Value.ToString());
        Console.WriteLine($"Auto-created active session: {created.Id.Value}");
        return (created, null, false, true);
    }

    static async Task<SessionState?> GetActiveSessionAsync(SqliteStorageInitializer store, RepositoryId repositoryId)
    {
        var path = GetActiveSessionPath(repositoryId);
        if (!IOFile.Exists(path))
        {
            return null;
        }

        var sessionIdText = (await IOFile.ReadAllTextAsync(path)).Trim();
        if (string.IsNullOrWhiteSpace(sessionIdText) || !Guid.TryParse(sessionIdText, out var sessionGuid))
        {
            return null;
        }

        return await store.GetSessionAsync(new GetSessionRequest(new SessionId(sessionGuid)));
    }

    static async Task SetActiveSessionAsync(SqliteStorageInitializer store, RepositoryId repositoryId, string sessionId)
    {
        var path = GetActiveSessionPath(repositoryId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await IOFile.WriteAllTextAsync(path, sessionId);
    }

    static string GetActiveSessionPath(RepositoryId repositoryId)
    {
        return Path.Combine(".devx", "current-session", repositoryId.Value.ToString(), "active");
    }
}
