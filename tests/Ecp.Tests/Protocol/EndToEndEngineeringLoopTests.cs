using System.Text.Json;
using Ecp.Domain;
using Ecp.Infrastructure.Storage.SQLite;
using Ecp.Indexer;
using Ecp.Protocol;
using Microsoft.Data.Sqlite;
using Xunit;
using IOFile = System.IO.File;

namespace Ecp.Tests.Protocol;

public sealed class EndToEndEngineeringLoopTests
{
    [Fact]
    public async Task ExecuteAsync_FullEngineeringLoop_StaysWithinProtocolAndPersistsState()
    {
        var repositoryRoot = Directory.CreateTempSubdirectory("ecp-e2e-test").FullName;
        CreateFixtureRepository(repositoryRoot);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(Path.GetTempPath(), $"ecp-e2e-{Guid.NewGuid():N}.db")
        }.ToString();

        var store = new SqliteStorageInitializer(connectionString);
        var indexer = new RepositoryIndexingService(store);
        await indexer.IndexAsync(repositoryRoot);

        var repositoryId = await GetRepositoryIdAsync(connectionString);
        var session = await store.CreateSessionAsync(new CreateSessionRequest(new RepositoryId(Guid.Parse(repositoryId))));
        var engine = new ProtocolEngine(new SearchService(store, store), store, store);

        var searchResponse = await engine.ExecuteAsync(new ProtocolRequest(
            "1.0",
            ProtocolOperations.SearchSymbol,
            session.Id.Value.ToString(),
            JsonSerializer.SerializeToElement(new { query = "Run" })));

        Assert.True(searchResponse.Success);
        Assert.NotNull(searchResponse.Data);

        var patchResponse = await engine.ExecuteAsync(new ProtocolRequest(
            "1.0",
            ProtocolOperations.Patch,
            session.Id.Value.ToString(),
            JsonSerializer.SerializeToElement(new
            {
                version = "1.0",
                operations = new[]
                {
                    new
                    {
                        operation = "CreateFile",
                        path = "src/SampleApp/Generated.cs",
                        content = """
                        namespace SampleApp;

                        public static class Generated
                        {
                            public static string Name() => "patched";
                        }
                        """
                    }
                }
            })));

        Assert.True(patchResponse.Success);

        var buildResponse = await engine.ExecuteAsync(new ProtocolRequest(
            "1.0",
            ProtocolOperations.RunBuild,
            session.Id.Value.ToString(),
            JsonDocument.Parse("{}").RootElement.Clone()));

        Assert.True(buildResponse.Success);

        var diagnosticsResponse = await engine.ExecuteAsync(new ProtocolRequest(
            "1.0",
            ProtocolOperations.GetDiagnostics,
            session.Id.Value.ToString(),
            JsonDocument.Parse("{}").RootElement.Clone()));

        Assert.True(diagnosticsResponse.Success);
        Assert.NotNull(diagnosticsResponse.Data);

        var testResponse = await engine.ExecuteAsync(new ProtocolRequest(
            "1.0",
            ProtocolOperations.RunTests,
            session.Id.Value.ToString(),
            JsonDocument.Parse("{}").RootElement.Clone()));

        Assert.True(testResponse.Success);

        var resumed = await store.GetSessionAsync(new GetSessionRequest(session.Id));
        Assert.NotNull(resumed);
        Assert.NotEmpty(resumed!.SearchHistory);
        Assert.NotEmpty(resumed.BuildTestHistory);
    }

    private static void CreateFixtureRepository(string repositoryRoot)
    {
        var projectRoot = Path.Combine(repositoryRoot, "src", "SampleApp");
        var testRoot = Path.Combine(repositoryRoot, "tests", "SampleApp.Tests");
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(testRoot);

        IOFile.WriteAllText(Path.Combine(repositoryRoot, "Sample.sln"), """
            Microsoft Visual Studio Solution File, Format Version 12.00
            # Visual Studio Version 17
            VisualStudioVersion = 17.0.31903.59
            MinimumVisualStudioVersion = 10.0.40219.1
            Global
            EndGlobal
            """);

        IOFile.WriteAllText(Path.Combine(projectRoot, "SampleApp.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);

        IOFile.WriteAllText(Path.Combine(projectRoot, "Program.cs"), """
            namespace SampleApp;

            public static class Program
            {
                public static string Run() => "initial";
            }
            """);

        IOFile.WriteAllText(Path.Combine(testRoot, "SampleApp.Tests.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <IsPackable>false</IsPackable>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
                <PackageReference Include="xunit" Version="2.9.2" />
                <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
              </ItemGroup>
              <ItemGroup>
                <ProjectReference Include="..\\..\\src\\SampleApp\\SampleApp.csproj" />
              </ItemGroup>
            </Project>
            """);

        IOFile.WriteAllText(Path.Combine(testRoot, "ProgramTests.cs"), """
            using SampleApp;
            using Xunit;

            public sealed class ProgramTests
            {
                [Fact]
                public void Run_ReturnsPatchedValue()
                {
                    Assert.Equal("patched", Program.Run());
                }
            }
            """);
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
