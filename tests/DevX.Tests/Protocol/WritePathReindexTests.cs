using DevX.Domain;
using DevX.Infrastructure.Storage.SQLite;
using DevX.Indexer;
using DevX.Protocol;
using Microsoft.Data.Sqlite;
using Xunit;
using IOFile = System.IO.File;

namespace DevX.Tests.Protocol;

/// <summary>Brief OSS12 item 2, the owner's ruling: every write re-indexes the files it touched,
/// and `--no-index` opts out. The harness used to tell agents to run `devx index update` by hand
/// after each edit -- 43 such calls in a series of 39 runs -- which is pure overhead when the
/// write can do it. These pin the two halves of that: the index follows a patch without a second
/// command, and the opt-out really does leave the index alone.</summary>
public sealed class WritePathReindexTests
{
    private const string Alpha = """
        namespace Sample;

        public sealed class Alpha
        {
            public void FirstMethod()
            {
            }
        }
        """;

    private static async Task<(string Root, SqliteStorageInitializer Store, ProtocolEngine Engine, string Session)> FixtureAsync(bool refreshIndexAfterEdit)
    {
        var repositoryRoot = Directory.CreateTempSubdirectory("devx-write-reindex").FullName;
        var projectRoot = Path.Combine(repositoryRoot, "src", "Sample");
        Directory.CreateDirectory(projectRoot);
        await IOFile.WriteAllTextAsync(Path.Combine(projectRoot, "Sample.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        await IOFile.WriteAllTextAsync(Path.Combine(projectRoot, "Alpha.cs"), Alpha);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(Path.GetTempPath(), $"devx-write-reindex-{Guid.NewGuid():N}.db"),
        }.ToString();
        var store = new SqliteStorageInitializer(connectionString);
        await new RepositoryIndexingService(store).IndexAsync(repositoryRoot);

        var session = await store.CreateSessionAsync(new CreateSessionRequest(await RepositoryIdAsync(connectionString)));
        var engine = new ProtocolEngine(
            new SearchService(store, store), store, store,
            new DotnetBuildTestService(), new PatchLifecycleService(), refreshIndexAfterEdit);
        return (repositoryRoot, store, engine, session.Id.Value.ToString());
    }

    private static async Task<RepositoryId> RepositoryIdAsync(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM repositories LIMIT 1;";
        return new RepositoryId(Guid.Parse((string)(await command.ExecuteScalarAsync())!));
    }

    private static async Task<bool> FindsAsync(SqliteStorageInitializer store, string name)
    {
        var result = await new SearchService(store, store).SearchSymbolAsync(new SearchSymbolRequest(name));
        return result.Symbols.Any(symbol => symbol.Name == name);
    }

    private static ProtocolRequest ReplaceMethod(string session, string replacement) => new(
        "1.0",
        ProtocolOperations.Patch,
        session,
        System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            version = "1.0",
            operations = new[]
            {
                new { operation = "ReplaceMethod", path = "src/Sample/Alpha.cs", methodName = "FirstMethod", replacementText = replacement },
            },
        }));

    [Fact]
    public async Task PatchApply_ReindexesTheEditedFileWithoutASecondCommand()
    {
        var (_, store, engine, session) = await FixtureAsync(refreshIndexAfterEdit: true);

        var response = await engine.ExecuteAsync(ReplaceMethod(session, "public void SecondMethod()\n{\n}"));

        Assert.True(response.Success);
        Assert.True(await FindsAsync(store, "SecondMethod"), "the new method is not in the index");
        Assert.False(await FindsAsync(store, "FirstMethod"), "the old method is still in the index");
    }

    [Fact]
    public async Task PatchApply_WithTheOptOut_LeavesTheIndexAlone()
    {
        var (root, store, engine, session) = await FixtureAsync(refreshIndexAfterEdit: false);

        var response = await engine.ExecuteAsync(ReplaceMethod(session, "public void SecondMethod()\n{\n}"));

        Assert.True(response.Success);
        // The edit landed...
        Assert.Contains("SecondMethod", await IOFile.ReadAllTextAsync(Path.Combine(root, "src", "Sample", "Alpha.cs")));
        // ...and the index still describes the previous text, which is what --no-index means.
        Assert.False(await FindsAsync(store, "SecondMethod"));
        Assert.True(await FindsAsync(store, "FirstMethod"));
    }

    [Fact]
    public async Task PatchDeleteFile_DropsTheFileFromTheIndex()
    {
        var (_, store, engine, session) = await FixtureAsync(refreshIndexAfterEdit: true);
        Assert.True(await FindsAsync(store, "FirstMethod"));

        var response = await engine.ExecuteAsync(new ProtocolRequest(
            "1.0",
            ProtocolOperations.Patch,
            session,
            System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                version = "1.0",
                operations = new[] { new { operation = "DeleteFile", path = "src/Sample/Alpha.cs" } },
            })));

        Assert.True(response.Success);
        // A deleted file must leave the index, not linger in it because ReindexFilesAsync reports
        // a missing file as an error.
        Assert.False(await FindsAsync(store, "FirstMethod"));
    }
}
