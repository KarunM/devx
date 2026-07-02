using Ecp.Domain;
using Ecp.Infrastructure.Storage.SQLite;
using Ecp.Indexer;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Ecp.Tests.Indexer;

public sealed class RepositoryIndexingServiceTests
{
    [Fact]
    public async Task IndexAsync_DiscoversProjectsSourceFilesAndSymbols()
    {
        var repositoryRoot = Directory.CreateTempSubdirectory("ecp-indexer-test").FullName;
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

        var databasePath = Path.Combine(Path.GetTempPath(), $"ecp-index-{Guid.NewGuid():N}.db");
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
