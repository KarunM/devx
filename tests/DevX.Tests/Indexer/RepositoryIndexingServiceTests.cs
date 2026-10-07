using DevX.Domain;
using DevX.Infrastructure.Storage.SQLite;
using DevX.Indexer;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevX.Tests.Indexer;

public sealed class RepositoryIndexingServiceTests
{
    [Fact]
    public async Task IndexAsync_IndexesGlobalNamespaceTypesInProject()
    {
        var root = Directory.CreateTempSubdirectory("devx-global-project").FullName;
        await System.IO.File.WriteAllTextAsync(Path.Combine(root, "App.csproj"), "<Project />");
        await System.IO.File.WriteAllTextAsync(Path.Combine(root, "Widget.cs"), "public class Widget { public int Frobnicate() => 1; }");
        var store = new SqliteStorageInitializer(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "index.db") }.ToString());

        try
        {
            await new RepositoryIndexingService(store).IndexAsync(root);
            var symbols = await store.FindSymbolsByNameAsync("Frobnicate");
            Assert.Contains(symbols, symbol => symbol.QualifiedName == "Widget.Frobnicate");
            Assert.DoesNotContain(symbols, symbol => symbol.QualifiedName.StartsWith('.'));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task IndexAsync_IndexesTopLevelFunctionsAfterUsingsAndReturnStatement()
    {
        var repositoryRoot = Directory.CreateTempSubdirectory("devx-top-level-indexer").FullName;
        var projectDirectory = Path.Combine(repositoryRoot, "src", "Sample");
        Directory.CreateDirectory(projectDirectory);
        await System.IO.File.WriteAllTextAsync(Path.Combine(projectDirectory, "Sample.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        await System.IO.File.WriteAllTextAsync(Path.Combine(projectDirectory, "Program.cs"), """
            using System;
            using System.Threading.Tasks;

            var exitCode = await RunAsync(args);
            return exitCode;

            static async Task<int> RunAsync(string[] args)
            {
                return await HealthAsync();
            }

            static async Task<int> HealthAsync()
            {
                return 0;
            }
            """);

        try
        {
            var store = new SqliteStorageInitializer(new SqliteConnectionStringBuilder { DataSource = Path.Combine(repositoryRoot, "index.db") }.ToString());
            await new RepositoryIndexingService(store).IndexAsync(repositoryRoot);

            var methods = await store.FindSymbolsByQualifiedNameAsync("Program.HealthAsync");

            Assert.Contains(methods, symbol => symbol.Name == "HealthAsync" && symbol.Kind == "Method");
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task IndexAsync_DiscoversProjectsSourceFilesAndSymbols()
    {
        var repositoryRoot = Directory.CreateTempSubdirectory("devx-indexer-test").FullName;
        var projectDirectory = Path.Combine(repositoryRoot, "src", "Sample");
        Directory.CreateDirectory(projectDirectory);

        await System.IO.File.WriteAllTextAsync(Path.Combine(projectDirectory, "Sample.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        await System.IO.File.WriteAllTextAsync(Path.Combine(projectDirectory, "Program.cs"), """
            namespace Sample;

            public static class Program
            {
                public static void Main() { }
                public void Run() { }
            }
            """);

        await System.IO.File.WriteAllTextAsync(Path.Combine(projectDirectory, "Helpers.cs"), """
            namespace Sample;

            public class Helpers
            {
                public void Run() { }
            }
            """);

        var databasePath = Path.Combine(Path.GetTempPath(), $"devx-index-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        var store = new SqliteStorageInitializer(connectionString);
        var indexer = new RepositoryIndexingService(store);

        await indexer.IndexAsync(repositoryRoot);
        await indexer.IndexAsync(repositoryRoot);

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        Assert.Equal(1, await CountAsync(connection, "repositories"));
        Assert.Equal(1, await CountAsync(connection, "projects"));
        Assert.Equal(2, await CountAsync(connection, "files"));
        Assert.Equal(3, await CountAsync(connection, "symbols", "kind", "Method"));

        var repositoryId = await SingleStringAsync(connection, "repositories");
        var projectId = await SingleStringAsync(connection, "projects");
        var fileId = await SingleStringAsync(connection, "files");

        await store.UpsertSymbolAsync(new Symbol(
            "demo-symbol-id",
            repositoryId,
            projectId,
            fileId,
            null,
            "Class",
            "Demo",
            "Sample.Demo",
            null));

        var qualifiedMatches = await store.FindSymbolsByQualifiedNameAsync("Sample.Demo");
        Assert.Contains(qualifiedMatches, symbol => symbol.Name == "Demo");
    }

    [Fact]
    public async Task IndexAsync_CanBeReRunWithoutChangingRepositoryId()
    {
        var repositoryRoot = Directory.CreateTempSubdirectory("devx-indexer-repeat").FullName;
        var projectDirectory = Path.Combine(repositoryRoot, "src", "Sample");
        Directory.CreateDirectory(projectDirectory);

        await System.IO.File.WriteAllTextAsync(Path.Combine(projectDirectory, "Sample.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        await System.IO.File.WriteAllTextAsync(Path.Combine(projectDirectory, "Program.cs"), """
            namespace Sample;

            public static class Program
            {
                public static void Main() { }
            }
            """);

        var databasePath = Path.Combine(Path.GetTempPath(), $"devx-index-repeat-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        var store = new SqliteStorageInitializer(connectionString);
        var indexer = new RepositoryIndexingService(store);

        await indexer.IndexAsync(repositoryRoot);
        await indexer.IndexAsync(repositoryRoot);

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        Assert.Equal(1, await CountAsync(connection, "repositories"));
        Assert.Equal(1, await CountAsync(connection, "projects"));
        Assert.Equal(1, await CountAsync(connection, "files"));
    }

    [Fact]
    public async Task IndexAsync_IndexesJsonForSnapshots()
    {
        var repositoryRoot = Directory.CreateTempSubdirectory("devx-json-index").FullName;
        await System.IO.File.WriteAllTextAsync(Path.Combine(repositoryRoot, "settings.json"), "{ \"featureFlag\": true }");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"devx-json-{Guid.NewGuid():N}.db") }.ToString();
        var store = new SqliteStorageInitializer(connectionString);

        await new RepositoryIndexingService(store).IndexAsync(repositoryRoot);
        var snapshot = await store.GetFileSnapshotAsync("settings.json");

        Assert.NotNull(snapshot);
        Assert.Equal("JSON", snapshot!.Language);
        Assert.Contains(snapshot.Fields, field => field.Name == "featureFlag");
    }

    private static async Task<int> CountAsync(SqliteConnection connection, string tableName)
        => await CountAsync(connection, tableName, null, null);

    private static async Task<int> CountAsync(SqliteConnection connection, string tableName, string? column, string? value)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = column is null
            ? $"SELECT COUNT(*) FROM {tableName};"
            : $"SELECT COUNT(*) FROM {tableName} WHERE {column} = $value;";
        if (column is not null)
        {
            command.Parameters.AddWithValue("$value", value!);
        }
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    private static async Task<string> SingleStringAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id FROM {tableName} LIMIT 1;";
        var result = await command.ExecuteScalarAsync();
        return Convert.ToString(result)!;
    }
}
