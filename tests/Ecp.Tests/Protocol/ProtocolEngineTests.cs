using System.Text.Json;
using Ecp.Domain;
using Ecp.Infrastructure.Storage.SQLite;
using Ecp.Indexer;
using Ecp.Protocol;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Ecp.Tests.Protocol;

public sealed class ProtocolEngineTests
{
    [Fact]
    public async Task ExecuteAsync_SearchSymbol_UpdatesSessionAndReturnsMetadataOnly()
    {
        var repositoryRoot = Directory.CreateTempSubdirectory("ecp-protocol-test").FullName;
        var projectDirectory = Path.Combine(repositoryRoot, "src", "Sample");
        Directory.CreateDirectory(projectDirectory);

        await System.IO.File.WriteAllTextAsync(Path.Combine(projectDirectory, "Sample.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        await System.IO.File.WriteAllTextAsync(Path.Combine(projectDirectory, "Alpha.cs"), """
            namespace Sample;
            public class Alpha { public void Run() { } }
            """);

        var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"ecp-protocol-{Guid.NewGuid():N}.db") }.ToString();
        var store = new SqliteStorageInitializer(connectionString);
        var indexer = new RepositoryIndexingService(store);
        await indexer.IndexAsync(repositoryRoot);

        var repositoryId = new RepositoryId(Guid.NewGuid());
        await store.UpsertRepositoryAsync(new Repository(repositoryId, "sample", repositoryRoot));
        var session = await store.CreateSessionAsync(new CreateSessionRequest(repositoryId));

        var engine = new ProtocolEngine(new SearchService(store, store), store, store);
        var request = new ProtocolRequest(
            "1.0",
            ProtocolOperations.SearchSymbol,
            session.Id.Value.ToString(),
            JsonSerializer.SerializeToElement(new { query = "Run" }));

        var response = await engine.ExecuteAsync(request);

        Assert.True(response.Success);
        Assert.Empty(response.Errors);
        Assert.NotNull(response.Data);
        Assert.DoesNotContain("public class Alpha", response.Data!.Value.GetRawText(), StringComparison.OrdinalIgnoreCase);

        var resumed = await store.GetSessionAsync(new GetSessionRequest(session.Id));
        Assert.NotNull(resumed);
        Assert.Contains("Run", resumed!.SearchHistory);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownOperation_ReturnsStructuredError()
    {
        var store = new SqliteStorageInitializer(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"ecp-protocol-{Guid.NewGuid():N}.db") }.ToString());
        await store.InitializeAsync();
        var repositoryId = new RepositoryId(Guid.NewGuid());
        await store.UpsertRepositoryAsync(new Repository(repositoryId, "sample", "/tmp/sample"));
        var session = await store.CreateSessionAsync(new CreateSessionRequest(repositoryId));

        var engine = new ProtocolEngine(new SearchService(store, store), store, store);
        var response = await engine.ExecuteAsync(new ProtocolRequest("1.0", "NOPE", session.Id.Value.ToString(), JsonSerializer.SerializeToElement(new { })));

        Assert.False(response.Success);
        Assert.Contains(response.Errors, error => error.Code == "InvalidOperation");
    }
}
