using Microsoft.Data.Sqlite;
using DevX.Domain;
using DomainFile = DevX.Domain.File;
using DomainType = DevX.Domain.Type;
using System.Text.Json;
using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DevX.Infrastructure.Storage.SQLite;

public sealed partial class SqliteStorageInitializer
{
    public async Task UpsertRepositoryAsync(Repository repository, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        // Nothing to write when the row is already current; a read-only database must still be readable.
        await using (var existing = connection.CreateCommand())
        {
            existing.CommandText = "SELECT 1 FROM repositories WHERE root_path = $root_path AND name = $name;";
            existing.Parameters.AddWithValue("$root_path", repository.RootPath);
            existing.Parameters.AddWithValue("$name", repository.Name);
            if (await existing.ExecuteScalarAsync(cancellationToken) is not null) return;
        }

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
            INSERT INTO file_index_observations (file_id, indexed_at) VALUES ($id, $indexed_at)
            ON CONFLICT(file_id) DO UPDATE SET indexed_at = excluded.indexed_at;
            """;
        command.Parameters.AddWithValue("$id", file.Id.Value.ToString());
        command.Parameters.AddWithValue("$project_id", file.ProjectId.Value.ToString());
        command.Parameters.AddWithValue("$path", file.Path);
        command.Parameters.AddWithValue("$indexed_at", DateTimeOffset.UtcNow.ToString("O"));
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

    public async Task UpsertReferenceAsync(Reference reference, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO references_table (id, file_id, target_symbol_id, caller_symbol_id, call_site_line)
            VALUES ($id, $file_id, $target_symbol_id, $caller_symbol_id, $call_site_line)
            ON CONFLICT(id) DO UPDATE SET
                file_id = excluded.file_id,
                target_symbol_id = excluded.target_symbol_id,
                caller_symbol_id = excluded.caller_symbol_id,
                call_site_line = excluded.call_site_line;
            """;
        command.Parameters.AddWithValue("$id", reference.Id.Value.ToString());
        command.Parameters.AddWithValue("$file_id", reference.FileId.Value.ToString());
        command.Parameters.AddWithValue("$target_symbol_id", reference.TargetSymbolId);
        command.Parameters.AddWithValue("$caller_symbol_id", reference.CallerSymbolId);
        command.Parameters.AddWithValue("$call_site_line", (object?)reference.CallSiteLine ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertSymbolAsync(Symbol symbol, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO symbols (id, repository_id, project_id, file_id, container_id, kind, name, qualified_name, signature, start_line, end_line)
            VALUES ($id, $repository_id, $project_id, $file_id, $container_id, $kind, $name, $qualified_name, $signature, $start_line, $end_line)
            ON CONFLICT(id) DO UPDATE SET
                repository_id = excluded.repository_id,
                project_id = excluded.project_id,
                file_id = excluded.file_id,
                container_id = excluded.container_id,
                kind = excluded.kind,
                name = excluded.name,
                qualified_name = excluded.qualified_name,
                signature = excluded.signature,
                start_line = excluded.start_line,
                end_line = excluded.end_line;
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
        command.Parameters.AddWithValue("$start_line", (object?)symbol.StartLine ?? DBNull.Value);
        command.Parameters.AddWithValue("$end_line", (object?)symbol.EndLine ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
