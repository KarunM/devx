using Ecp.Domain;
using Ecp.Infrastructure.Storage.SQLite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Ecp.Tests.Storage;

public sealed class SqliteSessionStoreTests
{
    [Fact]
    public async Task SessionLifecycle_PersistsWorkingSetAndHistory()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"ecp-session-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        var repositoryStore = new SqliteStorageInitializer(connectionString);
        var sessionStore = new SqliteSessionStore(connectionString);

        await repositoryStore.InitializeAsync();
        await sessionStore.InitializeAsync();

        var repositoryId = new RepositoryId(Guid.NewGuid());
        await repositoryStore.UpsertRepositoryAsync(new Repository(repositoryId, "sample", "/tmp/sample"));

        var session = await sessionStore.CreateSessionAsync(new CreateSessionRequest(repositoryId));
        await sessionStore.SaveSearchAsync(new SaveSearchRequest(session.Id, "Run"));
        await sessionStore.AddWorkingSetItemAsync(new AddWorkingSetItemRequest(session.Id, "src/Sample/Alpha.cs"));
        await sessionStore.MarkSymbolExpandedAsync(new MarkSymbolExpandedRequest(session.Id, "symbol-1"));
        await sessionStore.MarkFileExpandedAsync(new MarkFileExpandedRequest(session.Id, "file-1"));

        var resumed = await sessionStore.GetSessionAsync(new GetSessionRequest(session.Id));

        Assert.NotNull(resumed);
        Assert.Equal(session.Id, resumed!.Id);
        Assert.Single(resumed.SearchHistory);
        Assert.Contains("Run", resumed.SearchHistory);
        Assert.Contains("src/Sample/Alpha.cs", resumed.WorkingSet);
        Assert.Contains("symbol-1", resumed.ExpandedSymbols);
        Assert.Contains("file-1", resumed.ExpandedFiles);
    }
}
