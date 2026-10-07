using DevX.Domain;
using DevX.Infrastructure.Storage.SQLite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevX.Tests.Storage;

public sealed class SqliteSessionStoreTests
{
    [Fact]
    public async Task SessionLifecycle_PersistsWorkingSetAndHistory()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"devx-session-{Guid.NewGuid():N}.db");
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

    [Fact]
    public async Task OperationMetrics_ArePersistedPerSession()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"devx-session-metrics-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        var repositoryStore = new SqliteStorageInitializer(connectionString);
        var sessionStore = new SqliteSessionStore(connectionString);

        await repositoryStore.InitializeAsync();
        await sessionStore.InitializeAsync();

        var repositoryId = new RepositoryId(Guid.NewGuid());
        await repositoryStore.UpsertRepositoryAsync(new Repository(repositoryId, "sample", "/tmp/sample"));

        var session = await sessionStore.CreateSessionAsync(new CreateSessionRequest(repositoryId));
        await sessionStore.AddOperationMetricAsync(new OperationMetric("search symbol", DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(12), true, session.Id.Value.ToString(), "{\"AttemptedName\":\"Run\"}", "worker-2"));

        var metrics = await sessionStore.GetOperationMetricsAsync(session.Id);

        Assert.Single(metrics);
        Assert.Equal("search symbol", metrics[0].Operation);
        Assert.True(metrics[0].Success);
        Assert.Equal(session.Id.Value.ToString(), metrics[0].SessionId);
        Assert.Equal("worker-2", metrics[0].AgentLabel);
        Assert.Contains("AttemptedName", metrics[0].MetadataJson);
    }
}
