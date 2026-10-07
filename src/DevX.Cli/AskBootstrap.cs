using DevX.Domain;
using DevX.Infrastructure.Storage.SQLite;
using DevX.Indexer;

namespace DevX.Cli;

public sealed record AskBootstrapResult(IRepositoryStore Store, SessionState Session, AskRepoMap RepoMap, bool IndexCreated, bool SessionCreated);

public static class AskBootstrap
{
    public static async Task<AskBootstrapResult> InitializeAsync(string connectionString, bool allowReuse = true, CancellationToken cancellationToken = default)
    {
        var store = new SqliteStorageInitializer(connectionString);
        await store.InitializeAsync(cancellationToken);

        var repositoryRoot = Path.GetFullPath(Environment.CurrentDirectory);
        var repository = await store.GetRepositoryByRootPathAsync(repositoryRoot, cancellationToken);
        var indexCreated = false;

        if (repository is null)
        {
            var indexer = new RepositoryIndexingService(store);
            await indexer.IndexAsync(repositoryRoot, cancellationToken);
            indexCreated = true;
            repository = await store.GetRepositoryByRootPathAsync(repositoryRoot, cancellationToken)
                ?? throw new InvalidOperationException("Repository indexing did not create a repository record.");
        }

        var session = allowReuse ? await GetActiveSessionAsync(store, repository.Id, cancellationToken) : null;
        var sessionCreated = false;
        if (session is null)
        {
            session = await store.CreateSessionAsync(new CreateSessionRequest(repository.Id), cancellationToken);
            await SetActiveSessionAsync(store, repository.Id, session.Id.Value.ToString(), cancellationToken);
            sessionCreated = true;
        }

        var repoMap = await AskRepoMapBuilder.BuildAsync(store, session, cancellationToken);
        return new AskBootstrapResult(store, session, repoMap, indexCreated, sessionCreated);
    }

    private static async Task<SessionState?> GetActiveSessionAsync(SqliteStorageInitializer store, RepositoryId repositoryId, CancellationToken cancellationToken)
    {
        var path = GetActiveSessionPath(repositoryId);
        if (!System.IO.File.Exists(path)) return null;

        var sessionIdText = (await System.IO.File.ReadAllTextAsync(path, cancellationToken)).Trim();
        if (string.IsNullOrWhiteSpace(sessionIdText) || !Guid.TryParse(sessionIdText, out var sessionGuid))
        {
            return null;
        }

        return await store.GetSessionAsync(new GetSessionRequest(new SessionId(sessionGuid)), cancellationToken);
    }

    private static async Task SetActiveSessionAsync(SqliteStorageInitializer store, RepositoryId repositoryId, string sessionId, CancellationToken cancellationToken)
    {
        var path = GetActiveSessionPath(repositoryId);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await System.IO.File.WriteAllTextAsync(path, sessionId, cancellationToken);
    }

    private static string GetActiveSessionPath(RepositoryId repositoryId)
    {
        var safeId = repositoryId.Value.ToString();
        return Path.Combine(Environment.CurrentDirectory, ".devx", "active-sessions", $"{safeId}.txt");
    }
}
