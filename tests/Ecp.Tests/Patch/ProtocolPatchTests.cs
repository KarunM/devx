using System.Text.Json;
using Ecp.Domain;
using Ecp.Infrastructure.Storage.SQLite;
using Ecp.Indexer;
using Ecp.Protocol;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Ecp.Tests.Patch;

public sealed class ProtocolPatchTests
{
    [Fact]
    public async Task ExecuteAsync_PatchApply_CreatesFileInsideRepositoryRoot()
    {
        var repositoryRoot = Directory.CreateTempSubdirectory("ecp-patch-test").FullName;
        var projectDirectory = Path.Combine(repositoryRoot, "src", "Sample");
        Directory.CreateDirectory(projectDirectory);

        await System.IO.File.WriteAllTextAsync(Path.Combine(projectDirectory, "Sample.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"ecp-patch-{Guid.NewGuid():N}.db") }.ToString();
        var store = new SqliteStorageInitializer(connectionString);
        var indexer = new RepositoryIndexingService(store);
        await indexer.IndexAsync(repositoryRoot);

        var session = await store.CreateSessionAsync(new CreateSessionRequest(new RepositoryId(Guid.Parse(await GetRepositoryIdAsync(connectionString)))));

        var patchPath = Path.Combine(repositoryRoot, "patch.json");
        await System.IO.File.WriteAllTextAsync(patchPath, """
            {
              "version": "1.0",
              "operations": [
                { "operation": "CreateFile", "path": "src/Sample/NewFile.cs", "content": "namespace Sample; public static class NewFile { }" }
              ]
            }
            """);

        var engine = new ProtocolEngine(new SearchService(store, store), store, store);
        using var patchDocument = JsonDocument.Parse(await System.IO.File.ReadAllTextAsync(patchPath));
        var request = new ProtocolRequest("1.0", ProtocolOperations.Patch, session.Id.Value.ToString(), patchDocument.RootElement.Clone());
        var response = await engine.ExecuteAsync(request);

        Assert.True(response.Success);
        Assert.True(System.IO.File.Exists(Path.Combine(repositoryRoot, "src", "Sample", "NewFile.cs")));
    }

    [Fact]
    public async Task ExecuteAsync_PatchApply_RejectsPathsOutsideRepositoryRoot()
    {
        var repositoryRoot = Directory.CreateTempSubdirectory("ecp-patch-test").FullName;
        var projectDirectory = Path.Combine(repositoryRoot, "src", "Sample");
        Directory.CreateDirectory(projectDirectory);

        await System.IO.File.WriteAllTextAsync(Path.Combine(projectDirectory, "Sample.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"ecp-patch-{Guid.NewGuid():N}.db") }.ToString();
        var store = new SqliteStorageInitializer(connectionString);
        var indexer = new RepositoryIndexingService(store);
        await indexer.IndexAsync(repositoryRoot);

        var repositoryId = await GetRepositoryIdAsync(connectionString);
        var session = await store.CreateSessionAsync(new CreateSessionRequest(new RepositoryId(Guid.Parse(repositoryId))));

        var engine = new ProtocolEngine(new SearchService(store, store), store, store);
        using var patchDocument = JsonDocument.Parse("""
            {
              "version": "1.0",
              "operations": [
                { "operation": "CreateFile", "path": "../escape.cs", "content": "nope" }
              ]
            }
            """);
        var request = new ProtocolRequest(
            "1.0",
            ProtocolOperations.Patch,
            session.Id.Value.ToString(),
            patchDocument.RootElement.Clone());

        var response = await engine.ExecuteAsync(request);

        Assert.False(response.Success);
        Assert.Contains(response.Errors, error => error.Code == "ValidationError");
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
