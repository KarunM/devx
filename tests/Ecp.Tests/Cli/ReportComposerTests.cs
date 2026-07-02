using Ecp.Cli;
using Ecp.Domain;
using Ecp.Infrastructure.Storage.SQLite;
using Ecp.Indexer;
using Microsoft.Data.Sqlite;
using Xunit;
using IOFile = System.IO.File;

namespace Ecp.Tests.Cli;

public sealed class ReportComposerTests
{
    [Fact]
    public async Task ComposeAsync_ReportsUnavailableBuildTestMetricsUntilObserved()
    {
        var (store, connectionString) = CreateStore();
        await store.InitializeAsync();

        var repoRoot = CreateSampleRepo();
        var indexer = new RepositoryIndexingService(store);
        await indexer.IndexAsync(repoRoot);

        var repositoryId = await GetRepositoryIdAsync(connectionString);
        var session = await store.CreateSessionAsync(new CreateSessionRequest(repositoryId));

        var metrics = new List<OperationMetric>
        {
            new("session create", DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(10), true, session.Id.Value.ToString()),
            new("search symbol", DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(5), true, session.Id.Value.ToString())
        };

        var composer = new EcpReportComposer();
        var report = await composer.ComposeAsync(session, metrics, store);

        Assert.False(report.BuildTestMetrics.Available);
        Assert.Equal("Build/Test executed outside ECP.", report.BuildTestMetrics.Reason);
    }

    [Fact]
    public async Task ComposeAsync_ReportsObservedBuildTestMetrics()
    {
        var (store, connectionString) = CreateStore();
        await store.InitializeAsync();

        var repoRoot = CreateSampleRepo();
        var indexer = new RepositoryIndexingService(store);
        await indexer.IndexAsync(repoRoot);

        var repositoryId = await GetRepositoryIdAsync(connectionString);
        var session = await store.CreateSessionAsync(new CreateSessionRequest(repositoryId));
        var metrics = new List<OperationMetric>
        {
            new("build", DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(10), true, session.Id.Value.ToString()),
            new("test", DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(5), true, session.Id.Value.ToString())
        };

        var composer = new EcpReportComposer();
        var report = await composer.ComposeAsync(session, metrics, store);

        Assert.True(report.BuildTestMetrics.Available);
        Assert.Equal(1, report.BuildTestMetrics.BuildCount);
        Assert.Equal(1, report.BuildTestMetrics.SuccessfulBuilds);
        Assert.Equal(1, report.BuildTestMetrics.TestRuns);
        Assert.Equal(0, report.EfficiencySummary.RepairIterations);
    }

    [Fact]
    public async Task ComposeAsync_CountsFailedValidationAttemptsAsRepairIterations()
    {
        var (store, connectionString) = CreateStore();
        await store.InitializeAsync();

        var repoRoot = CreateSampleRepo();
        var indexer = new RepositoryIndexingService(store);
        await indexer.IndexAsync(repoRoot);

        var repositoryId = await GetRepositoryIdAsync(connectionString);
        var session = await store.CreateSessionAsync(new CreateSessionRequest(repositoryId));
        var metrics = new List<OperationMetric>
        {
            new("build", DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(10), false, session.Id.Value.ToString()),
            new("test", DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(5), true, session.Id.Value.ToString()),
            new("test", DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(5), false, session.Id.Value.ToString())
        };

        var composer = new EcpReportComposer();
        var report = await composer.ComposeAsync(session, metrics, store);

        Assert.Equal(2, report.EfficiencySummary.RepairIterations);
    }

    [Fact]
    public async Task ComposeAsync_ReportsWorkingSetAsArray()
    {
        var (store, connectionString) = CreateStore();
        await store.InitializeAsync();

        var repoRoot = CreateSampleRepo();
        var indexer = new RepositoryIndexingService(store);
        await indexer.IndexAsync(repoRoot);

        var repositoryId = await GetRepositoryIdAsync(connectionString);
        var session = await store.CreateSessionAsync(new CreateSessionRequest(repositoryId));
        await store.AddWorkingSetItemAsync(new AddWorkingSetItemRequest(session.Id, "id1"));
        await store.AddWorkingSetItemAsync(new AddWorkingSetItemRequest(session.Id, "id2"));
        var resumed = await store.GetSessionAsync(new GetSessionRequest(session.Id));

        var composer = new EcpReportComposer();
        var report = await composer.ComposeAsync(resumed!, [], store);

        Assert.Equal(["id1", "id2"], report.SessionSummary.WorkingSet);
    }

    private static (SqliteStorageInitializer Store, string ConnectionString) CreateStore()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"ecp-report-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        return (new SqliteStorageInitializer(connectionString), connectionString);
    }

    private static string CreateSampleRepo()
    {
        var repoRoot = Directory.CreateTempSubdirectory("ecp-report-repo").FullName;
        var projectRoot = Path.Combine(repoRoot, "src", "Sample");
        Directory.CreateDirectory(projectRoot);

        IOFile.WriteAllText(Path.Combine(projectRoot, "Sample.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        IOFile.WriteAllText(Path.Combine(projectRoot, "Program.cs"), """
            namespace Sample;
            public static class Program { public static void Run() { } }
            """);

        return repoRoot;
    }

    private static async Task<RepositoryId> GetRepositoryIdAsync(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM repositories LIMIT 1;";
        var result = await command.ExecuteScalarAsync();
        return new RepositoryId(Guid.Parse((string)result!));
    }
}
