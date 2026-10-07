using System.Text.Json;
using DevX.Domain;
using DevX.Infrastructure.Storage.SQLite;
using DevX.Indexer;
using DevX.Protocol;
using Microsoft.Data.Sqlite;
using Xunit;
using IOFile = System.IO.File;

namespace DevX.Tests.Protocol;

public sealed class McpReferenceServerIntegrationTests
{
    [Fact]
    public async Task InitializeAsync_ListsReferenceTools()
    {
        var (server, _, _) = await CreateServerAsync("devx-mcp-init");

        var response = await server.InitializeAsync();

        Assert.Equal("2025-06-18", response.ProtocolVersion);
        Assert.Contains(response.Tools, tool => tool.Name == "search" && tool.CapabilityId == "search");
        Assert.Contains(response.Tools, tool => tool.Name == "read" && tool.CapabilityId == "read");
        Assert.Contains(response.Tools, tool => tool.Name == "patch.validate" && tool.CapabilityId == "patch.validate");
        Assert.Contains(response.Tools, tool => tool.Name == "patch.apply" && tool.CapabilityId == "patch.apply");
        Assert.Contains(response.Tools, tool => tool.Name == "report" && tool.CapabilityId == "report");
    }

    [Fact]
    public async Task CallToolAsync_SearchAndRead_ReturnSuccessfulPayloads()
    {
        var (server, session, _) = await CreateServerAsync("devx-mcp-read");

        var search = await server.CallToolAsync("search", JsonSerializer.SerializeToElement(new
        {
            sessionId = session.Id.Value.ToString(),
            kind = "symbol",
            query = "Run"
        }));

        Assert.False(search.IsError);
        Assert.True(search.Content.GetProperty("success").GetBoolean());

        var read = await server.CallToolAsync("read", JsonSerializer.SerializeToElement(new
        {
            sessionId = session.Id.Value.ToString(),
            kind = "file",
            target = "src/Sample/Program.cs"
        }));

        Assert.False(read.IsError);
        Assert.True(read.Content.GetProperty("success").GetBoolean());
        Assert.Contains("Program", read.Content.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CallToolAsync_PatchValidateApplyAndReport_ReturnSuccessfulPayloads()
    {
        var (server, session, repositoryRoot) = await CreateServerAsync("devx-mcp-patch");

        var patch = JsonSerializer.SerializeToElement(new
        {
            version = "1.0",
            operations = new[]
            {
                new
                {
                    operation = "CreateFile",
                    path = "src/Sample/Generated.cs",
                    content = "namespace Sample; public static class Generated { public static string Name() => \"mcp\"; }"
                }
            }
        });

        var validate = await server.CallToolAsync("patch.validate", JsonSerializer.SerializeToElement(new
        {
            sessionId = session.Id.Value.ToString(),
            patch
        }));

        Assert.False(validate.IsError);
        Assert.True(validate.Content.GetProperty("success").GetBoolean());

        var apply = await server.CallToolAsync("patch.apply", JsonSerializer.SerializeToElement(new
        {
            sessionId = session.Id.Value.ToString(),
            patch
        }));

        Assert.False(apply.IsError);
        Assert.True(apply.Content.GetProperty("success").GetBoolean());
        Assert.True(IOFile.Exists(Path.Combine(repositoryRoot, "src", "Sample", "Generated.cs")));

        var report = await server.CallToolAsync("report", JsonSerializer.SerializeToElement(new
        {
            sessionId = session.Id.Value.ToString()
        }));

        Assert.False(report.IsError);
        Assert.Equal(session.Id.Value.ToString(), report.Content.GetProperty("sessionSummary").GetProperty("sessionId").GetString());
        Assert.True(report.Content.GetProperty("operationLog").GetArrayLength() >= 0);
    }

    private static async Task<(McpReferenceServer Server, SessionState Session, string RepositoryRoot)> CreateServerAsync(string prefix)
    {
        var repositoryRoot = Directory.CreateTempSubdirectory(prefix).FullName;
        var projectDirectory = Path.Combine(repositoryRoot, "src", "Sample");
        Directory.CreateDirectory(projectDirectory);

        await IOFile.WriteAllTextAsync(Path.Combine(projectDirectory, "Sample.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        await IOFile.WriteAllTextAsync(Path.Combine(projectDirectory, "Program.cs"), """
            namespace Sample;
            public static class Program
            {
                public static void Run() { }
            }
            """);

        var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"devx-mcp-{Guid.NewGuid():N}.db") }.ToString();
        var store = new SqliteStorageInitializer(connectionString);
        var indexer = new RepositoryIndexingService(store);
        await indexer.IndexAsync(repositoryRoot);

        var repositoryId = await GetRepositoryIdAsync(connectionString);
        var session = await store.CreateSessionAsync(new CreateSessionRequest(new RepositoryId(Guid.Parse(repositoryId))));
        var engine = new ProtocolEngine(new SearchService(store, store), store, store);
        var server = new McpReferenceServer(engine, store, store);
        return (server, session, repositoryRoot);
    }

    private static async Task<string> GetRepositoryIdAsync(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM repositories LIMIT 1;";
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
