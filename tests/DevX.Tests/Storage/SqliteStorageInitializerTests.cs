using DevX.Infrastructure.Storage.SQLite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevX.Tests.Storage;

public sealed class SqliteStorageInitializerTests
{
    [Fact]
    public async Task InitializeAsync_CreatesCanonicalStorageSchema()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"devx-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString();

        var initializer = new SqliteStorageInitializer(connectionString);

        await initializer.InitializeAsync();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM sqlite_master
            WHERE type = 'table'
            ORDER BY name;
            """;

        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        Assert.Contains("repositories", tables);
        Assert.Contains("projects", tables);
        Assert.Contains("files", tables);
        Assert.Contains("namespaces", tables);
        Assert.Contains("types", tables);
        Assert.Contains("methods", tables);
        Assert.Contains("properties", tables);
        Assert.Contains("fields", tables);
        Assert.Contains("references_table", tables);
        Assert.Contains("diagnostics", tables);
        Assert.Contains("tests", tables);
        Assert.Contains("sessions", tables);
    }
}
