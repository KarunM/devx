using Microsoft.Data.Sqlite;
using Ecp.Domain;
using DomainFile = Ecp.Domain.File;
using DomainType = Ecp.Domain.Type;
using System.Text.Json;

namespace Ecp.Infrastructure.Storage.SQLite;

public sealed class SqliteStorageInitializer : IRepositoryStore, ISymbolStore, ISessionStore
{
    private readonly string _connectionString;

    public SqliteStorageInitializer(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS repositories (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                root_path TEXT NOT NULL UNIQUE
            );

            CREATE TABLE IF NOT EXISTS projects (
                id TEXT PRIMARY KEY,
                repository_id TEXT NOT NULL,
                name TEXT NOT NULL,
                path TEXT NOT NULL,
                FOREIGN KEY (repository_id) REFERENCES repositories(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS files (
                id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL,
                path TEXT NOT NULL,
                FOREIGN KEY (project_id) REFERENCES projects(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS namespaces (
                id TEXT PRIMARY KEY,
                file_id TEXT NOT NULL,
                name TEXT NOT NULL,
                FOREIGN KEY (file_id) REFERENCES files(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS types (
                id TEXT PRIMARY KEY,
                namespace_id TEXT NOT NULL,
                name TEXT NOT NULL,
                FOREIGN KEY (namespace_id) REFERENCES namespaces(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS methods (
                id TEXT PRIMARY KEY,
                type_id TEXT NOT NULL,
                name TEXT NOT NULL,
                FOREIGN KEY (type_id) REFERENCES types(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS properties (
                id TEXT PRIMARY KEY,
                type_id TEXT NOT NULL,
                name TEXT NOT NULL,
                FOREIGN KEY (type_id) REFERENCES types(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS fields (
                id TEXT PRIMARY KEY,
                type_id TEXT NOT NULL,
                name TEXT NOT NULL,
                FOREIGN KEY (type_id) REFERENCES types(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS symbols (
                id TEXT PRIMARY KEY,
                repository_id TEXT NOT NULL,
                project_id TEXT NOT NULL,
                file_id TEXT NOT NULL,
                container_id TEXT NULL,
                kind TEXT NOT NULL,
                name TEXT NOT NULL,
                qualified_name TEXT NOT NULL,
                signature TEXT NULL,
                FOREIGN KEY (repository_id) REFERENCES repositories(id) ON DELETE CASCADE,
                FOREIGN KEY (project_id) REFERENCES projects(id) ON DELETE CASCADE,
                FOREIGN KEY (file_id) REFERENCES files(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS references_table (
                id TEXT PRIMARY KEY,
                file_id TEXT NOT NULL,
                target_symbol_id TEXT NOT NULL,
                FOREIGN KEY (file_id) REFERENCES files(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS diagnostics (
                id TEXT PRIMARY KEY,
                target_kind TEXT NOT NULL,
                message TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS tests (
                id TEXT PRIMARY KEY,
                file_id TEXT NOT NULL,
                name TEXT NOT NULL,
                FOREIGN KEY (file_id) REFERENCES files(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS sessions (
                id TEXT PRIMARY KEY,
                repository_id TEXT NOT NULL,
                working_set TEXT NULL,
                search_history TEXT NOT NULL,
                expanded_files TEXT NOT NULL,
                expanded_symbols TEXT NOT NULL,
                diagnostics_history TEXT NOT NULL,
                patch_history TEXT NOT NULL,
                build_test_history TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_files_path ON files(path);
            CREATE INDEX IF NOT EXISTS ix_files_path_lower ON files(lower(path));
            CREATE INDEX IF NOT EXISTS ix_types_name ON types(name);
            CREATE INDEX IF NOT EXISTS ix_methods_name ON methods(name);
            CREATE INDEX IF NOT EXISTS ix_references_target ON references_table(target_symbol_id);
            CREATE INDEX IF NOT EXISTS ix_symbols_name ON symbols(name);
            CREATE INDEX IF NOT EXISTS ix_symbols_qualified_name ON symbols(qualified_name);
            """;

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task UpsertRepositoryAsync(Repository repository, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO repositories (id, name, root_path)
            VALUES ($id, $name, $root_path)
            ON CONFLICT(root_path) DO UPDATE SET
                name = excluded.name;
            """;
        command.Parameters.AddWithValue("$id", repository.Id.Value.ToString());
        command.Parameters.AddWithValue("$name", repository.Name);
        command.Parameters.AddWithValue("$root_path", repository.RootPath);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertProjectAsync(Project project, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO projects (id, repository_id, name, path)
            VALUES ($id, $repository_id, $name, $path)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                path = excluded.path,
                repository_id = excluded.repository_id;
            """;
        command.Parameters.AddWithValue("$id", project.Id.Value.ToString());
        command.Parameters.AddWithValue("$repository_id", project.RepositoryId.Value.ToString());
        command.Parameters.AddWithValue("$name", project.Name);
        command.Parameters.AddWithValue("$path", project.Path);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertFileAsync(DomainFile file, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO files (id, project_id, path)
            VALUES ($id, $project_id, $path)
            ON CONFLICT(id) DO UPDATE SET
                project_id = excluded.project_id,
                path = excluded.path;
            """;
        command.Parameters.AddWithValue("$id", file.Id.Value.ToString());
        command.Parameters.AddWithValue("$project_id", file.ProjectId.Value.ToString());
        command.Parameters.AddWithValue("$path", file.Path);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertNamespaceAsync(Namespace @namespace, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO namespaces (id, file_id, name)
            VALUES ($id, $file_id, $name)
            ON CONFLICT(id) DO UPDATE SET
                file_id = excluded.file_id,
                name = excluded.name;
            """;
        command.Parameters.AddWithValue("$id", @namespace.Id.Value.ToString());
        command.Parameters.AddWithValue("$file_id", @namespace.FileId.Value.ToString());
        command.Parameters.AddWithValue("$name", @namespace.Name);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertTypeAsync(DomainType type, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO types (id, namespace_id, name)
            VALUES ($id, $namespace_id, $name)
            ON CONFLICT(id) DO UPDATE SET
                namespace_id = excluded.namespace_id,
                name = excluded.name;
            """;
        command.Parameters.AddWithValue("$id", type.Id.Value.ToString());
        command.Parameters.AddWithValue("$namespace_id", type.NamespaceId.Value.ToString());
        command.Parameters.AddWithValue("$name", type.Name);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertMethodAsync(Method method, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO methods (id, type_id, name)
            VALUES ($id, $type_id, $name)
            ON CONFLICT(id) DO UPDATE SET
                type_id = excluded.type_id,
                name = excluded.name;
            """;
        command.Parameters.AddWithValue("$id", method.Id.Value.ToString());
        command.Parameters.AddWithValue("$type_id", method.TypeId.Value.ToString());
        command.Parameters.AddWithValue("$name", method.Name);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertPropertyAsync(Property property, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO properties (id, type_id, name)
            VALUES ($id, $type_id, $name)
            ON CONFLICT(id) DO UPDATE SET
                type_id = excluded.type_id,
                name = excluded.name;
            """;
        command.Parameters.AddWithValue("$id", property.Id.Value.ToString());
        command.Parameters.AddWithValue("$type_id", property.TypeId.Value.ToString());
        command.Parameters.AddWithValue("$name", property.Name);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertFieldAsync(Field field, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO fields (id, type_id, name)
            VALUES ($id, $type_id, $name)
            ON CONFLICT(id) DO UPDATE SET
                type_id = excluded.type_id,
                name = excluded.name;
            """;
        command.Parameters.AddWithValue("$id", field.Id.Value.ToString());
        command.Parameters.AddWithValue("$type_id", field.TypeId.Value.ToString());
        command.Parameters.AddWithValue("$name", field.Name);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertSymbolAsync(Symbol symbol, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO symbols (id, repository_id, project_id, file_id, container_id, kind, name, qualified_name, signature)
            VALUES ($id, $repository_id, $project_id, $file_id, $container_id, $kind, $name, $qualified_name, $signature)
            ON CONFLICT(id) DO UPDATE SET
                repository_id = excluded.repository_id,
                project_id = excluded.project_id,
                file_id = excluded.file_id,
                container_id = excluded.container_id,
                kind = excluded.kind,
                name = excluded.name,
                qualified_name = excluded.qualified_name,
                signature = excluded.signature;
            """;
        command.Parameters.AddWithValue("$id", symbol.Id);
        command.Parameters.AddWithValue("$repository_id", symbol.RepositoryId);
        command.Parameters.AddWithValue("$project_id", symbol.ProjectId);
        command.Parameters.AddWithValue("$file_id", symbol.FileId);
        command.Parameters.AddWithValue("$container_id", (object?)symbol.ContainerId ?? DBNull.Value);
        command.Parameters.AddWithValue("$kind", symbol.Kind);
        command.Parameters.AddWithValue("$name", symbol.Name);
        command.Parameters.AddWithValue("$qualified_name", symbol.QualifiedName);
        command.Parameters.AddWithValue("$signature", (object?)symbol.Signature ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Symbol>> FindSymbolsByNameAsync(string name, CancellationToken cancellationToken = default)
        => await FindSymbolsAsync("name", name, cancellationToken);

    public async Task<IReadOnlyList<Symbol>> FindSymbolsByQualifiedNameAsync(string qualifiedName, CancellationToken cancellationToken = default)
        => await FindSymbolsAsync("qualified_name", qualifiedName, cancellationToken);

    public async Task<IReadOnlyList<Symbol>> FindSymbolsContainingAsync(string query, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, repository_id, project_id, file_id, container_id, kind, name, qualified_name, signature
            FROM symbols
            WHERE lower(name) LIKE lower($query) OR lower(qualified_name) LIKE lower($query)
            ORDER BY qualified_name;
            """;
        command.Parameters.AddWithValue("$query", $"%{query}%");

        var results = new List<Symbol>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new Symbol(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return results;
    }

    public async Task<IReadOnlyList<FileSearchResult>> SearchFilesAsync(string query, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.id, f.path, p.name, r.name
            FROM files f
            INNER JOIN projects p ON p.id = f.project_id
            INNER JOIN repositories r ON r.id = p.repository_id
            WHERE lower(f.path) LIKE lower($query)
            ORDER BY CASE WHEN lower(f.path) = lower($exact) THEN 0 ELSE 1 END, f.path;
            """;
        command.Parameters.AddWithValue("$query", $"%{query}%");
        command.Parameters.AddWithValue("$exact", query);

        var results = new List<FileSearchResult>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new FileSearchResult(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        return results;
    }

    public async Task<string?> GetRepositoryRootPathAsync(RepositoryId repositoryId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT root_path
            FROM repositories
            WHERE id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", repositoryId.Value.ToString());
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result?.ToString();
    }

    private async Task<IReadOnlyList<Symbol>> FindSymbolsAsync(string columnName, string value, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, repository_id, project_id, file_id, container_id, kind, name, qualified_name, signature
            FROM symbols
            WHERE {columnName} = $value
            ORDER BY qualified_name;
            """;
        command.Parameters.AddWithValue("$value", value);

        var results = new List<Symbol>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new Symbol(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return results;
    }

    public Task<SessionState> CreateSessionAsync(CreateSessionRequest request, CancellationToken cancellationToken = default)
        => SessionStore().CreateSessionAsync(request, cancellationToken);

    public Task<SessionState?> GetSessionAsync(GetSessionRequest request, CancellationToken cancellationToken = default)
        => SessionStore().GetSessionAsync(request, cancellationToken);

    public Task SaveSearchAsync(SaveSearchRequest request, CancellationToken cancellationToken = default)
        => SessionStore().SaveSearchAsync(request, cancellationToken);

    public Task AddWorkingSetItemAsync(AddWorkingSetItemRequest request, CancellationToken cancellationToken = default)
        => SessionStore().AddWorkingSetItemAsync(request, cancellationToken);

    public Task MarkSymbolExpandedAsync(MarkSymbolExpandedRequest request, CancellationToken cancellationToken = default)
        => SessionStore().MarkSymbolExpandedAsync(request, cancellationToken);

    public Task MarkFileExpandedAsync(MarkFileExpandedRequest request, CancellationToken cancellationToken = default)
        => SessionStore().MarkFileExpandedAsync(request, cancellationToken);

    public Task AddDiagnosticAsync(AddDiagnosticRequest request, CancellationToken cancellationToken = default)
        => SessionStore().AddDiagnosticAsync(request, cancellationToken);

    public Task AddExecutionAsync(AddExecutionRequest request, CancellationToken cancellationToken = default)
        => SessionStore().AddExecutionAsync(request, cancellationToken);

    private SqliteSessionStore SessionStore() => new(_connectionString);
}
