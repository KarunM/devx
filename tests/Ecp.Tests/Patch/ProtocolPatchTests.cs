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

    [Fact]
    public async Task ExecuteAsync_PatchPreview_ReturnsDiffWithoutChangingRepository()
    {
        var repositoryRoot = Directory.CreateTempSubdirectory("ecp-patch-preview-test").FullName;
        var projectDirectory = Path.Combine(repositoryRoot, "src", "Sample");
        Directory.CreateDirectory(projectDirectory);

        await System.IO.File.WriteAllTextAsync(Path.Combine(projectDirectory, "Sample.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"ecp-patch-preview-{Guid.NewGuid():N}.db") }.ToString();
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
                { "operation": "CreateFile", "path": "src/Sample/NewFile.cs", "content": "namespace Sample; public static class NewFile { }" }
              ]
            }
            """);

        var request = new ProtocolRequest("1.0", "PATCH_PREVIEW", session.Id.Value.ToString(), patchDocument.RootElement.Clone());
        var response = await engine.ExecuteAsync(request);

        Assert.True(response.Success);
        Assert.Contains("NewFile.cs", response.Data!.Value.GetRawText());
        Assert.False(System.IO.File.Exists(Path.Combine(repositoryRoot, "src", "Sample", "NewFile.cs")));
    }

    [Fact]
    public async Task ExecuteAsync_PatchValidate_ReturnsInvalidForOutsideRepositoryPath()
    {
        var repositoryRoot = Directory.CreateTempSubdirectory("ecp-patch-validate-test").FullName;
        var projectDirectory = Path.Combine(repositoryRoot, "src", "Sample");
        Directory.CreateDirectory(projectDirectory);

        await System.IO.File.WriteAllTextAsync(Path.Combine(projectDirectory, "Sample.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"ecp-patch-validate-{Guid.NewGuid():N}.db") }.ToString();
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
                { "operation": "CreateFile", "path": "../escape.txt", "content": "nope" }
              ]
            }
            """);

        var request = new ProtocolRequest("1.0", "PATCH_VALIDATE", session.Id.Value.ToString(), patchDocument.RootElement.Clone());
        var response = await engine.ExecuteAsync(request);

        Assert.True(response.Success);
        Assert.Contains("outside the repository root", response.Data!.Value.GetRawText());
    }

    [Fact]
    public async Task ExecuteAsync_PatchRollback_RestoresReplaceMethodBeforeState()
    {
        var repositoryRoot = Directory.CreateTempSubdirectory("ecp-patch-rollback-method-test").FullName;
        var projectDirectory = Path.Combine(repositoryRoot, "src", "Sample");
        Directory.CreateDirectory(projectDirectory);

        var filePath = Path.Combine(projectDirectory, "Sample.cs");
        await System.IO.File.WriteAllTextAsync(filePath, """
            namespace Sample;

            public sealed class Sample
            {
                public void Hello()
                {
                    System.Console.WriteLine("before");
                }
            }
            """);

        var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"ecp-patch-rollback-method-{Guid.NewGuid():N}.db") }.ToString();
        var store = new SqliteStorageInitializer(connectionString);
        var indexer = new RepositoryIndexingService(store);
        await indexer.IndexAsync(repositoryRoot);

        var repositoryId = await GetRepositoryIdAsync(connectionString);
        var session = await store.CreateSessionAsync(new CreateSessionRequest(new RepositoryId(Guid.Parse(repositoryId))));

        var engine = new ProtocolEngine(new SearchService(store, store), store, store);
        var beforeState = await System.IO.File.ReadAllTextAsync(filePath);
        var patchPayload = JsonSerializer.Serialize(new
        {
            version = "1.0",
            operations = new[]
            {
                new
                {
                    operation = "ReplaceMethod",
                    path = "src/Sample/Sample.cs",
                    methodName = "Hello",
                    replacementText = "public void Hello() { System.Console.WriteLine(\"after\"); }",
                    beforeState
                }
            }
        });
        using var patchDocument = JsonDocument.Parse(patchPayload);

        var applyRequest = new ProtocolRequest("1.0", ProtocolOperations.Patch, session.Id.Value.ToString(), patchDocument.RootElement.Clone());
        var applyResponse = await engine.ExecuteAsync(applyRequest);
        Assert.True(applyResponse.Success);
        Assert.Contains("after", await System.IO.File.ReadAllTextAsync(filePath));

        var rollbackRequest = new ProtocolRequest("1.0", ProtocolOperations.PatchRollback, session.Id.Value.ToString(), patchDocument.RootElement.Clone());
        var rollbackResponse = await engine.ExecuteAsync(rollbackRequest);

        Assert.True(rollbackResponse.Success);
        Assert.Contains("before", await System.IO.File.ReadAllTextAsync(filePath));
        Assert.Contains("FilesRestored", rollbackResponse.Data!.Value.GetRawText());
    }

    [Fact]
    public async Task ExecuteAsync_PatchRollback_RemovesCreatedFile()
    {
        var repositoryRoot = Directory.CreateTempSubdirectory("ecp-patch-rollback-test").FullName;
        var projectDirectory = Path.Combine(repositoryRoot, "src", "Sample");
        Directory.CreateDirectory(projectDirectory);

        await System.IO.File.WriteAllTextAsync(Path.Combine(projectDirectory, "Sample.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"ecp-patch-rollback-{Guid.NewGuid():N}.db") }.ToString();
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
                { "operation": "CreateFile", "path": "src/Sample/NewFile.cs", "content": "namespace Sample; public static class NewFile { }" }
              ]
            }
            """);

        var applyRequest = new ProtocolRequest("1.0", ProtocolOperations.Patch, session.Id.Value.ToString(), patchDocument.RootElement.Clone());
        var applyResponse = await engine.ExecuteAsync(applyRequest);
        Assert.True(applyResponse.Success);
        Assert.True(System.IO.File.Exists(Path.Combine(repositoryRoot, "src", "Sample", "NewFile.cs")));

        var rollbackRequest = new ProtocolRequest("1.0", ProtocolOperations.PatchRollback, session.Id.Value.ToString(), patchDocument.RootElement.Clone());
        var rollbackResponse = await engine.ExecuteAsync(rollbackRequest);

        Assert.True(rollbackResponse.Success);
        Assert.False(System.IO.File.Exists(Path.Combine(repositoryRoot, "src", "Sample", "NewFile.cs")));
        Assert.Contains("FilesRestored", rollbackResponse.Data!.Value.GetRawText());
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
