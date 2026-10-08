using DevX.Domain;
using DevX.Infrastructure.Storage.SQLite;
using IOFile = System.IO.File;

namespace DevX.Cli;

public static class AutomaticSessionResolver
{
    public static async Task<SessionState> GetOrCreateAsync(SqliteStorageInitializer store, string root, Repository repository)
    {
        await store.UpsertRepositoryAsync(repository);
        var sessionFile = Path.Combine(root, ".devx", "auto-session");
        SessionState? session = null;
        if (IOFile.Exists(sessionFile) && Guid.TryParse((await IOFile.ReadAllTextAsync(sessionFile)).Trim(), out var sessionGuid))
            session = await store.GetSessionAsync(new GetSessionRequest(new SessionId(sessionGuid)));
        if (session is null)
        {
            session = await store.CreateSessionAsync(new CreateSessionRequest(repository.Id));
            Directory.CreateDirectory(Path.GetDirectoryName(sessionFile)!);
            await IOFile.WriteAllTextAsync(sessionFile, session.Id.Value.ToString());
        }
        return session;
    }
}
