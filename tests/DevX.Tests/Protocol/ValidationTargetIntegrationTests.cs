using System.Text.Json;
using DevX.Domain;
using DevX.Infrastructure.Storage.SQLite;
using DevX.Indexer;
using DevX.Protocol;
using Microsoft.Data.Sqlite;
using Xunit;
using IOFile = System.IO.File;

namespace DevX.Tests.Protocol;

public sealed class ValidationTargetIntegrationTests
{
    [Fact]
    public async Task ExecuteAsync_RunBuild_UsesExplicitProjectTarget()
    {
        var (engine, session, repositoryRoot) = await CreateEngineAsync("devx-validation-target-build");

        var response = await engine.ExecuteAsync(new ProtocolRequest(
            "1.0",
            ProtocolOperations.RunBuild,
            session.Id.Value.ToString(),
            JsonSerializer.SerializeToElement(new
            {
                runtime = "dotnet",
                target = "src/GoodApp/GoodApp.csproj"
            })));

        Assert.True(response.Success);
        var payload = response.Data!.Value.Deserialize<ValidationExecutionResponse>();
        Assert.NotNull(payload);
        Assert.Equal("build", payload!.CapabilityId);
        Assert.Equal("dotnet", payload.Context.RuntimeFamily);
        Assert.Equal("src/GoodApp/GoodApp.csproj", payload.Context.ProjectPath);
        Assert.Equal("src/GoodApp/GoodApp.csproj", payload.Target.ProjectPath);
        Assert.Contains($"build \"{Path.Combine(repositoryRoot, "src", "GoodApp", "GoodApp.csproj")}\"", payload.Execution.Command, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_RunTests_UsesExplicitTestTarget()
    {
        var (engine, session, repositoryRoot) = await CreateEngineAsync("devx-validation-target-test");

        var response = await engine.ExecuteAsync(new ProtocolRequest(
            "1.0",
            ProtocolOperations.RunTests,
            session.Id.Value.ToString(),
            JsonSerializer.SerializeToElement(new
            {
                runtime = "dotnet",
                target = "tests/GoodApp.Tests/GoodApp.Tests.csproj"
            })));

        Assert.True(response.Success);
        var payload = response.Data!.Value.Deserialize<ValidationExecutionResponse>();
        Assert.NotNull(payload);
        Assert.Equal("test", payload!.CapabilityId);
        Assert.Equal("tests/GoodApp.Tests/GoodApp.Tests.csproj", payload.Context.ProjectPath);
        Assert.Contains($"test \"{Path.Combine(repositoryRoot, "tests", "GoodApp.Tests", "GoodApp.Tests.csproj")}\"", payload.Execution.Command, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_RunBuild_ReturnsStructuredErrorWhenTargetDoesNotExist()
    {
        var (engine, session, _) = await CreateEngineAsync("devx-validation-target-missing");

        var response = await engine.ExecuteAsync(new ProtocolRequest(
            "1.0",
            ProtocolOperations.RunBuild,
            session.Id.Value.ToString(),
            JsonSerializer.SerializeToElement(new
            {
                runtime = "dotnet",
                target = "src/Missing/Missing.csproj"
            })));

        Assert.False(response.Success);
        Assert.Contains(response.Errors, error => error.Code == "ValidationError");
        Assert.Contains(response.Errors, error => error.Message.Contains("No runtime target matched", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<(ProtocolEngine Engine, SessionState Session, string RepositoryRoot)> CreateEngineAsync(string prefix)
    {
        var repositoryRoot = Directory.CreateTempSubdirectory(prefix).FullName;
        var goodProjectRoot = Path.Combine(repositoryRoot, "src", "GoodApp");
        var brokenProjectRoot = Path.Combine(repositoryRoot, "src", "BrokenApp");
        var testProjectRoot = Path.Combine(repositoryRoot, "tests", "GoodApp.Tests");
        Directory.CreateDirectory(goodProjectRoot);
        Directory.CreateDirectory(brokenProjectRoot);
        Directory.CreateDirectory(testProjectRoot);

        IOFile.WriteAllText(Path.Combine(goodProjectRoot, "GoodApp.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);

        IOFile.WriteAllText(Path.Combine(goodProjectRoot, "Program.cs"), """
            namespace GoodApp;
            public static class Program
            {
                public static string Run() => "ok";
            }
            """);

        IOFile.WriteAllText(Path.Combine(brokenProjectRoot, "BrokenApp.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        IOFile.WriteAllText(Path.Combine(brokenProjectRoot, "Broken.cs"), """
            namespace BrokenApp;
            public static class Broken
            {
                public static void Run( => 1;
            }
            """);

        IOFile.WriteAllText(Path.Combine(testProjectRoot, "GoodApp.Tests.csproj"), """
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
                <ProjectReference Include="..\\..\\src\\GoodApp\\GoodApp.csproj" />
              </ItemGroup>
            </Project>
            """);

        IOFile.WriteAllText(Path.Combine(testProjectRoot, "ProgramTests.cs"), """
            using GoodApp;
            using Xunit;

            public sealed class ProgramTests
            {
                [Fact]
                public void Run_ReturnsOk()
                {
                    Assert.Equal("ok", Program.Run());
                }
            }
            """);

        var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"devx-validation-target-{Guid.NewGuid():N}.db") }.ToString();
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
