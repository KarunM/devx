using System.Text.Json;
using DevX.Domain;
using DevX.Infrastructure.Storage.SQLite;
using DevX.Indexer;
using DevX.Protocol;
using Microsoft.Data.Sqlite;
using Xunit;
using IOFile = System.IO.File;

namespace DevX.Tests.Patch;

public sealed class PatchLifecycleContractTests
{
    [Fact]
    public async Task ValidateAndPreview_ReportSharedPatchCapabilitySummary()
    {
        var (engine, session, repositoryRoot) = await CreateEngineAsync("devx-patch-lifecycle-preview");

        using var patchDocument = JsonDocument.Parse("""
            {
              "version": "1.0",
              "operations": [
                { "operation": "CreateFile", "path": "src/Sample/NewFile.cs", "content": "namespace Sample; public static class NewFile { }" }
              ]
            }
            """);

        var validateResponse = await engine.ExecuteAsync(new ProtocolRequest("1.0", "PATCH_VALIDATE", session.Id.Value.ToString(), patchDocument.RootElement.Clone()));
        var previewResponse = await engine.ExecuteAsync(new ProtocolRequest("1.0", "PATCH_PREVIEW", session.Id.Value.ToString(), patchDocument.RootElement.Clone()));

        Assert.True(validateResponse.Success);
        Assert.True(previewResponse.Success);

        var validate = validateResponse.Data!.Value.Deserialize<PatchValidateResponse>();
        var preview = previewResponse.Data!.Value.Deserialize<PatchPreviewResponse>();

        Assert.NotNull(validate);
        Assert.NotNull(preview);
        Assert.Equal("patch", validate!.Summary.CapabilityId);
        Assert.Equal("patch", preview!.Summary.CapabilityId);
        Assert.Equal(validate.Summary.OperationClasses, preview.Summary.OperationClasses);
        Assert.Equal(validate.Summary.FilesAffected, preview.Summary.FilesAffected);
        Assert.True(validate.Summary.RollbackSupported);
        Assert.Equal("Supported", validate.Summary.Contracts[0].RollbackAvailability);
        Assert.Equal(Path.Combine(repositoryRoot, "src", "Sample", "NewFile.cs"), Path.Combine(repositoryRoot, validate.Summary.FilesAffected[0]));
    }

    [Fact]
    public async Task RollbackAvailability_IsExplicitForUnsupportedOperations()
    {
        var (engine, session, _) = await CreateEngineAsync("devx-patch-lifecycle-rollback");

        using var patchDocument = JsonDocument.Parse("""
            {
              "version": "1.0",
              "operations": [
                { "operation": "DeleteFile", "path": "src/Sample/Program.cs" }
              ]
            }
            """);

        var validateResponse = await engine.ExecuteAsync(new ProtocolRequest("1.0", "PATCH_VALIDATE", session.Id.Value.ToString(), patchDocument.RootElement.Clone()));

        Assert.True(validateResponse.Success);
        var validate = validateResponse.Data!.Value.Deserialize<PatchValidateResponse>();
        Assert.NotNull(validate);
        Assert.False(validate!.Summary.RollbackSupported);
        Assert.Equal("Unavailable", validate.Summary.Contracts[0].RollbackAvailability);
        Assert.Contains("backup", validate.Summary.Contracts[0].RollbackReason!, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(ProtocolEngine Engine, SessionState Session, string RepositoryRoot)> CreateEngineAsync(string prefix)
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
            public static class Program { public static void Run() { } }
            """);

        var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"devx-patch-contract-{Guid.NewGuid():N}.db") }.ToString();
        var store = new SqliteStorageInitializer(connectionString);
        var indexer = new RepositoryIndexingService(store);
        await indexer.IndexAsync(repositoryRoot);

        var repositoryId = await GetRepositoryIdAsync(connectionString);
        var session = await store.CreateSessionAsync(new CreateSessionRequest(new RepositoryId(Guid.Parse(repositoryId))));
        var engine = new ProtocolEngine(new SearchService(store, store), store, store);
        return (engine, session, repositoryRoot);
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
