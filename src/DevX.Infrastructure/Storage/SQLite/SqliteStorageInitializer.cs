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

            CREATE TABLE IF NOT EXISTS file_index_observations (
                file_id TEXT PRIMARY KEY,
                indexed_at TEXT NOT NULL
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
                start_line INTEGER NULL,
                end_line INTEGER NULL,
                FOREIGN KEY (repository_id) REFERENCES repositories(id) ON DELETE CASCADE,
                FOREIGN KEY (project_id) REFERENCES projects(id) ON DELETE CASCADE,
                FOREIGN KEY (file_id) REFERENCES files(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS references_table (
                id TEXT PRIMARY KEY,
                file_id TEXT NOT NULL,
                target_symbol_id TEXT NOT NULL,
                caller_symbol_id TEXT NOT NULL,
                call_site_line INTEGER NULL,
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

        await EnsureReferenceColumnsAsync(connection, cancellationToken);
        await EnsureSymbolColumnsAsync(connection, cancellationToken);
    }

    private static async Task EnsureSymbolColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(symbols);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) columns.Add(reader.GetString(1));
        }
        foreach (var column in new[] { "start_line", "end_line" })
        {
            if (columns.Contains(column)) continue;
            await using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE symbols ADD COLUMN {column} INTEGER NULL;";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task EnsureReferenceColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(references_table);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(reader.GetString(1));
            }
        }

        if (!columns.Contains("caller_symbol_id"))
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE references_table ADD COLUMN caller_symbol_id TEXT NULL;";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!columns.Contains("call_site_line"))
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE references_table ADD COLUMN call_site_line INTEGER NULL;";
            await alter.ExecuteNonQueryAsync(cancellationToken);
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
            SELECT s.id, s.repository_id, s.project_id, s.file_id, s.container_id, s.kind, s.name, s.qualified_name, s.signature,
                   s.start_line, s.end_line, f.path
            FROM symbols s
            INNER JOIN files f ON f.id = s.file_id
            WHERE lower(s.name) LIKE lower($query) OR lower(s.qualified_name) LIKE lower($query)
            ORDER BY s.qualified_name;
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
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetInt32(9),
                reader.IsDBNull(10) ? null : reader.GetInt32(10),
                reader.GetString(11)));
        }

        return results;
    }

    public async Task<IReadOnlyList<Symbol>> FindClosestSymbolsAsync(string query, int limit = 5, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id, s.repository_id, s.project_id, s.file_id, s.container_id, s.kind, s.name, s.qualified_name, s.signature,
                   s.start_line, s.end_line, f.path
            FROM symbols s
            INNER JOIN files f ON f.id = s.file_id
            ORDER BY s.qualified_name;
            """;

        var candidates = new List<Symbol>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            candidates.Add(new Symbol(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetString(6),
                reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetInt32(9), reader.IsDBNull(10) ? null : reader.GetInt32(10), reader.GetString(11)));
        }

        var needle = query.Split('.').LastOrDefault() ?? query;
        var maximumDistance = Math.Max(2, needle.Length / 3);
        return candidates
            .Select(symbol => (Symbol: symbol, Distance: EditDistance(needle, symbol.Name)))
            .Where(item => item.Distance <= maximumDistance)
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Symbol.QualifiedName, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, limit))
            .Select(item => item.Symbol)
            .ToList();
    }

    private static int EditDistance(string left, string right)
    {
        left = left.ToLowerInvariant();
        right = right.ToLowerInvariant();
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        for (var i = 1; i <= left.Length; i++)
        {
            var current = new int[right.Length + 1];
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
            }
            previous = current;
        }
        return previous[right.Length];
    }

    public async Task<IReadOnlyList<FileSearchResult>> SearchFilesAsync(string query, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.id, f.path, p.name, r.name, r.root_path
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
            results.Add(new FileSearchResult(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                System.IO.File.Exists(Path.Combine(reader.GetString(4), reader.GetString(1))) ? null : "file missing"));
        }

        return results;
    }

    public async Task<IReadOnlyList<string>> GetIndexedFilePathsAsync(string root, CancellationToken token = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT f.path FROM files f JOIN projects p ON p.id = f.project_id JOIN repositories r ON r.id = p.repository_id WHERE r.root_path = $root ORDER BY f.path;";
        command.Parameters.AddWithValue("$root", Path.GetFullPath(root));
        var paths = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) paths.Add(reader.GetString(0));
        return paths;
    }

    public async Task<IReadOnlyList<ContentSearchResult>> SearchFileContentsAsync(
        string query,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
        {
            return [];
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.id, f.path, r.root_path
            FROM files f
            INNER JOIN projects p ON p.id = f.project_id
            INNER JOIN repositories r ON r.id = p.repository_id
            ORDER BY f.path;
            """;

        var indexedFiles = new List<(string Id, string Path, string Root)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                indexedFiles.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        var matches = new List<ContentSearchResult>();
        foreach (var file in indexedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(Path.Combine(file.Root, file.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!System.IO.File.Exists(fullPath))
            {
                continue;
            }

            string[] lines;
            try
            {
                lines = await System.IO.File.ReadAllLinesAsync(fullPath, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                continue;
            }

            for (var index = 0; index < lines.Length; index++)
            {
                var column = lines[index].IndexOf(query, StringComparison.OrdinalIgnoreCase);
                if (column < 0)
                {
                    continue;
                }

                matches.Add(new ContentSearchResult(file.Id, file.Path, index + 1, column + 1, lines[index].Trim()));
                if (matches.Count >= limit)
                {
                    return matches;
                }
            }
        }

        return matches;
    }

    public async Task<FileSnapshot?> GetFileSnapshotAsync(string fileIdOrPath, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.id, f.path, p.name, p.path, r.name, r.root_path
            FROM files f
            INNER JOIN projects p ON p.id = f.project_id
            INNER JOIN repositories r ON r.id = p.repository_id
            WHERE f.id = $id_or_path OR lower(f.path) = lower($id_or_path)
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id_or_path", fileIdOrPath);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var fileId = reader.GetString(0);
        var path = reader.GetString(1);
        var projectName = reader.GetString(2);
        var projectPath = reader.GetString(3);
        var rootPath = reader.GetString(5);

        var namespaces = await QueryListAsync(connection, """
            SELECT id, name
            FROM namespaces
            WHERE file_id = $file_id
            ORDER BY name;
            """, fileId, cancellationToken, row => new FileNamespaceSnapshot(row.GetString(0), row.GetString(1)));

        var types = await QueryListAsync(connection, """
            SELECT t.id, t.name, n.name
            FROM types t
            INNER JOIN namespaces n ON n.id = t.namespace_id
            WHERE n.file_id = $file_id
            ORDER BY n.name, t.name;
            """, fileId, cancellationToken, row => new FileTypeSnapshot(row.GetString(0), row.GetString(1), row.GetString(2)));

        var methods = await QueryListAsync(connection, """
            SELECT m.id, m.name, t.name, n.name
            FROM methods m
            INNER JOIN types t ON t.id = m.type_id
            INNER JOIN namespaces n ON n.id = t.namespace_id
            WHERE n.file_id = $file_id
            ORDER BY n.name, t.name, m.name;
            """, fileId, cancellationToken, row => new FileMethodSnapshot(row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3)));

        var properties = await QueryListAsync(connection, """
            SELECT p.id, p.name, t.name, n.name
            FROM properties p
            INNER JOIN types t ON t.id = p.type_id
            INNER JOIN namespaces n ON n.id = t.namespace_id
            WHERE n.file_id = $file_id
            ORDER BY n.name, t.name, p.name;
            """, fileId, cancellationToken, row => new FilePropertySnapshot(row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3)));

        var fields = await QueryListAsync(connection, """
            SELECT f.id, f.name, t.name, n.name
            FROM fields f
            INNER JOIN types t ON t.id = f.type_id
            INNER JOIN namespaces n ON n.id = t.namespace_id
            WHERE n.file_id = $file_id
            ORDER BY n.name, t.name, f.name;
            """, fileId, cancellationToken, row => new FileFieldSnapshot(row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3)));

        var loc = await CountLinesAsync(rootPath, path, cancellationToken);
        var dependencies = await GetProjectDependenciesAsync(rootPath, projectPath, cancellationToken);
        return new FileSnapshot(fileId, projectName, path, StructuredSymbolReader.LanguageOf(path), loc, namespaces, types, methods, properties, fields, dependencies);
    }

    public async Task<IReadOnlyList<MethodSummary>> FindMethodSummariesAsync(string query, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id, r.name, p.name, f.path, n.name, t.name, s.name, s.qualified_name, s.kind
            FROM symbols s
            INNER JOIN files f ON f.id = s.file_id
            INNER JOIN projects p ON p.id = f.project_id
            INNER JOIN repositories r ON r.id = p.repository_id
            INNER JOIN types t ON t.id = s.container_id
            INNER JOIN namespaces n ON n.id = t.namespace_id
            WHERE s.kind IN ('Method', 'Constructor')
              AND (
                  lower(s.name) = lower($exact)
                  OR lower(s.qualified_name) = lower($exact)
                  OR lower(s.id) = lower($exact)
                  OR lower(s.name) LIKE lower($query)
                  OR lower(s.qualified_name) LIKE lower($query)
              )
            ORDER BY
                CASE
                    WHEN lower(s.name) = lower($exact) THEN 0
                    WHEN lower(s.qualified_name) = lower($exact) THEN 1
                    WHEN lower(s.id) = lower($exact) THEN 2
                    ELSE 3
                END,
                s.qualified_name,
                s.name;
            """;
        command.Parameters.AddWithValue("$exact", query);
        command.Parameters.AddWithValue("$query", $"%{query}%");

        var results = new List<MethodSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new MethodSummary(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8)));
        }

        return results;
    }

    public async Task<IReadOnlyList<MethodSource>> FindMethodSourcesAsync(string query, CancellationToken cancellationToken = default)
    {
        var summaries = await FindMethodSummariesAsync(query, cancellationToken);
        var results = new List<MethodSource>();

        foreach (var summary in summaries)
        {
            var source = await TryGetMethodSourceAsync(summary, cancellationToken);
            if (source is not null)
            {
                results.Add(source);
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<TypeSummary>> FindTypeSummariesAsync(string query, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.id, t.name, n.name, f.path, p.name, r.name, r.root_path
            FROM types t
            INNER JOIN namespaces n ON n.id = t.namespace_id
            INNER JOIN files f ON f.id = n.file_id
            INNER JOIN projects p ON p.id = f.project_id
            INNER JOIN repositories r ON r.id = p.repository_id
            WHERE lower(t.id) = lower($exact)
               OR lower(t.name) = lower($exact)
               OR lower(n.name || '.' || t.name) = lower($exact)
               OR lower(n.name || '.' || t.name) LIKE lower($query)
               OR lower(t.name) LIKE lower($query)
            ORDER BY
                CASE
                    WHEN lower(t.id) = lower($exact) THEN 0
                    WHEN lower(n.name || '.' || t.name) = lower($exact) THEN 1
                    WHEN lower(t.name) = lower($exact) THEN 2
                    ELSE 3
                END,
                n.name,
                t.name;
            """;
        command.Parameters.AddWithValue("$exact", query);
        command.Parameters.AddWithValue("$query", $"%{query}%");

        var rows = new List<(string Id, string Name, string Namespace, string FilePath, string Project, string Repository, string RootPath)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6)));
            }
        }

        var results = new List<TypeSummary>();
        foreach (var row in rows)
        {
            var fullPath = Path.Combine(row.RootPath, row.FilePath.Replace('/', Path.DirectorySeparatorChar));
            if (!System.IO.File.Exists(fullPath))
            {
                continue;
            }

            var sourceText = await System.IO.File.ReadAllTextAsync(fullPath, cancellationToken);
            if (StructuredSymbolReader.CanParse(fullPath))
            {
                var parsed = StructuredSymbolReader.Parse(fullPath);
                var pythonType = parsed.Types.FirstOrDefault(type => string.Equals(type.Name, row.Name, StringComparison.Ordinal));
                if (pythonType is not null)
                {
                    var pythonSummary = BuildPythonTypeSummary(row, pythonType, sourceText);
                    if (pythonSummary is not null)
                    {
                        results.Add(pythonSummary);
                    }
                }

                continue;
            }

            var tree = CSharpSyntaxTree.ParseText(sourceText);
            var root = await tree.GetRootAsync(cancellationToken);
            var typeNode = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
                .FirstOrDefault(node => string.Equals(node.Identifier.ValueText, row.Name, StringComparison.Ordinal));
            if (typeNode is null)
            {
                continue;
            }

            var summary = await BuildTypeSummaryAsync(row, typeNode, sourceText, cancellationToken);
            if (summary is not null)
            {
                results.Add(summary);
            }
        }

        return results;
    }

    public async Task<TypeSummary?> FindTypeSummaryByIdAsync(string typeId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id, s.name, s.qualified_name, s.kind, n.name, p.name, f.path, r.root_path
            FROM symbols s
            INNER JOIN files f ON f.id = s.file_id
            INNER JOIN projects p ON p.id = f.project_id
            INNER JOIN repositories r ON r.id = p.repository_id
            LEFT JOIN types t ON t.id = s.container_id
            LEFT JOIN namespaces n ON n.id = COALESCE(t.namespace_id, (SELECT namespace_id FROM types WHERE id = s.container_id))
            WHERE lower(s.id) = lower($type_id)
              AND s.kind IN ('Type', 'Class', 'Record', 'Struct', 'Interface', 'Enum', 'Delegate');
            """;
        command.Parameters.AddWithValue("$type_id", typeId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var row = (
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            reader.GetString(6),
            reader.GetString(5),
            reader.GetString(7),
            reader.GetString(7));

        var fullPath = Path.Combine(row.Item7, row.Item4.Replace('/', Path.DirectorySeparatorChar));
        if (!System.IO.File.Exists(fullPath))
        {
            return null;
        }

        var sourceText = await System.IO.File.ReadAllTextAsync(fullPath, cancellationToken);
        if (StructuredSymbolReader.CanParse(fullPath))
        {
            var parsed = StructuredSymbolReader.Parse(fullPath);
            var pythonType = parsed.Types.FirstOrDefault(type => string.Equals(type.Name, row.Item2, StringComparison.Ordinal));
            return pythonType is null ? null : BuildPythonTypeSummary(
                (row.Item1, row.Item2, row.Item3, row.Item4, row.Item5, row.Item6, row.Item7),
                pythonType,
                sourceText);
        }

        var tree = CSharpSyntaxTree.ParseText(sourceText);
        var root = await tree.GetRootAsync(cancellationToken);
        var typeNode = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(node => string.Equals(node.Identifier.ValueText, row.Item2, StringComparison.Ordinal));

        if (typeNode is null)
        {
            return null;
        }

        return await BuildTypeSummaryAsync((row.Item1, row.Item2, row.Item3, row.Item4, row.Item5, row.Item6, row.Item7), typeNode, sourceText, cancellationToken);
    }

    public async Task<IReadOnlyList<CallerSummary>> FindCallersAsync(string query, CancellationToken cancellationToken = default)
    {
        var targets = await FindMethodsForCallersAsync(query, cancellationToken);
        if (targets.Count == 0)
        {
            return [];
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id, s.name, s.qualified_name, n.name, p.name, f.path, t.name, r.root_path
            FROM symbols s
            INNER JOIN files f ON f.id = s.file_id
            INNER JOIN projects p ON p.id = f.project_id
            INNER JOIN repositories r ON r.id = p.repository_id
            INNER JOIN types t ON t.id = s.container_id
            INNER JOIN namespaces n ON n.id = t.namespace_id
            WHERE s.kind IN ('Method', 'Constructor')
            ORDER BY s.qualified_name;
            """;
        var candidates = new List<(string Id, string Name, string QualifiedName, string Namespace, string Project, string FilePath, string TypeName, string RootPath)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                candidates.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7)));
            }
        }

        var results = new List<CallerSummary>();
        foreach (var target in targets)
        {
            foreach (var candidate in candidates)
            {
                if (candidate.Id == target.Id || !System.IO.File.Exists(Path.Combine(candidate.RootPath, candidate.FilePath.Replace('/', Path.DirectorySeparatorChar))))
                {
                    continue;
                }

                var fullPath = Path.Combine(candidate.RootPath, candidate.FilePath.Replace('/', Path.DirectorySeparatorChar));
                var sourceText = await System.IO.File.ReadAllTextAsync(fullPath, cancellationToken);
                if (!sourceText.Contains(target.Name + "(", StringComparison.Ordinal))
                {
                    continue;
                }

                results.Add(new CallerSummary(
                    candidate.Id,
                    candidate.Name,
                    candidate.QualifiedName,
                    candidate.Namespace,
                    candidate.Project,
                    candidate.FilePath,
                    candidate.TypeName,
                    null));
            }
        }

        return results.DistinctBy(result => result.CallerMethodId).ToList();
    }

    public async Task<IReadOnlyList<ReferenceSummary>> FindReferencesAsync(string query, CancellationToken cancellationToken = default)
    {
        var targetSearch = await FindSymbolsContainingAsync(query, cancellationToken);
        if (targetSearch.Count == 0)
        {
            return [];
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id, s.name, s.qualified_name, s.kind, n.name, p.name, f.path, t.name, r.root_path
            FROM symbols s
            INNER JOIN files f ON f.id = s.file_id
            INNER JOIN projects p ON p.id = f.project_id
            INNER JOIN repositories r ON r.id = p.repository_id
            INNER JOIN types t ON t.id = s.container_id
            INNER JOIN namespaces n ON n.id = t.namespace_id
            ORDER BY s.qualified_name;
            """;
        var candidates = new List<(string Id, string Name, string QualifiedName, string Kind, string Namespace, string Project, string FilePath, string TypeName, string RootPath)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                candidates.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8)));
            }
        }

        var results = new List<ReferenceSummary>();
        var addedIds = new HashSet<string>();
        foreach (var target in targetSearch)
        {
            foreach (var candidate in candidates)
            {
                if (candidate.Id == target.Id || !System.IO.File.Exists(Path.Combine(candidate.RootPath, candidate.FilePath.Replace('/', Path.DirectorySeparatorChar))))
                {
                    continue;
                }

                // If candidate is the same as the search hit
                var fullPath = Path.Combine(candidate.RootPath, candidate.FilePath.Replace('/', Path.DirectorySeparatorChar));
                var sourceText = await System.IO.File.ReadAllTextAsync(fullPath, cancellationToken);
                
                // Very basic heuristic for finding references without symbol analysis (similar to the caller behavior)
                if (sourceText.Contains(target.Name, StringComparison.Ordinal) && !addedIds.Contains(candidate.Id))
                {
                    addedIds.Add(candidate.Id);
                    results.Add(new ReferenceSummary(
                        System.Guid.NewGuid().ToString(),
                        candidate.Name,
                        candidate.QualifiedName,
                        candidate.Kind,
                        candidate.Kind == "Method" || candidate.Kind == "Constructor" ? candidate.Name : "",
                        candidate.TypeName,
                        candidate.Namespace,
                        candidate.Project,
                        candidate.FilePath,
                        null
                    ));
                }
            }
        }

        return results;
    }
    public async Task<IReadOnlyList<CalleeSummary>> FindCalleesAsync(string query, CancellationToken cancellationToken = default)
    {
        var methods = await FindMethodsForCallersAsync(query, cancellationToken);
        if (methods.Count == 0)
        {
            return [];
        }

        var results = new List<CalleeSummary>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id, s.name, s.qualified_name, n.name, p.name, f.path, t.name
            FROM symbols s
            INNER JOIN files f ON f.id = s.file_id
            INNER JOIN projects p ON p.id = f.project_id
            INNER JOIN repositories r ON r.id = p.repository_id
            INNER JOIN types t ON t.id = s.container_id
            INNER JOIN namespaces n ON n.id = t.namespace_id
            WHERE s.kind IN ('Method', 'Constructor')
            ORDER BY s.qualified_name;
            """;
        var candidates = new List<(string Id, string Name, string QualifiedName, string Namespace, string Project, string FilePath, string TypeName)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                candidates.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6)));
            }
        }

        foreach (var method in methods)
        {
            var source = await FindMethodSourcesAsync(method.QualifiedName, cancellationToken);
            if (source.Count == 0)
            {
                continue;
            }

            var sourceText = source[0].Source;
            var lines = sourceText.Split('\n');
            foreach (var candidate in candidates)
            {
                if (candidate.Id == method.Id || !sourceText.Contains(candidate.Name + "(", StringComparison.Ordinal))
                {
                    continue;
                }

                results.Add(new CalleeSummary(candidate.Id, candidate.Name, candidate.QualifiedName, candidate.Namespace, candidate.Project, candidate.FilePath, candidate.TypeName, null));
            }
        }

        return results.DistinctBy(result => result.CalleeMethodId).ToList();
    }

    private async Task<IReadOnlyList<Symbol>> FindMethodsForCallersAsync(string query, CancellationToken cancellationToken)
    {
        var byName = await FindSymbolsByNameAsync(query, cancellationToken);
        var byQualified = await FindSymbolsByQualifiedNameAsync(query, cancellationToken);
        return byName.Concat(byQualified).Where(symbol => symbol.Kind is "Method" or "Constructor").DistinctBy(symbol => symbol.Id).ToList();
    }

    private async Task<MethodSource?> TryGetMethodSourceAsync(MethodSummary summary, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.root_path
            FROM files f
            INNER JOIN projects p ON p.id = f.project_id
            INNER JOIN repositories r ON r.id = p.repository_id
            WHERE f.path = $file_path
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$file_path", summary.FilePath);

        var rootPath = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return null;
        }

        var fullPath = Path.Combine(rootPath, summary.FilePath.Replace('/', Path.DirectorySeparatorChar));
        if (!System.IO.File.Exists(fullPath))
        {
            return null;
        }

        var sourceText = await System.IO.File.ReadAllTextAsync(fullPath, cancellationToken);

        if (StructuredSymbolReader.CanParse(fullPath))
        {
            return TryGetStructuredMethodSource(summary, fullPath, sourceText);
        }

        var tree = CSharpSyntaxTree.ParseText(sourceText);
        var root = await tree.GetRootAsync(cancellationToken);

        var method = root.DescendantNodes()
            .OfType<BaseMethodDeclarationSyntax>()
            .FirstOrDefault(node =>
            {
                var containingType = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                var ns = node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
                var nodeName = node switch
                {
                    MethodDeclarationSyntax m => m.Identifier.ValueText,
                    ConstructorDeclarationSyntax c => c.Identifier.ValueText,
                    _ => string.Empty
                };
                return string.Equals(ns, summary.Namespace, StringComparison.Ordinal)
                    && string.Equals(containingType?.Identifier.ValueText, summary.TypeName, StringComparison.Ordinal)
                    && string.Equals(nodeName, summary.Name, StringComparison.Ordinal);
            });

        if (method is null)
        {
            return null;
        }

        var lineSpan = method.GetLocation().GetLineSpan();
        return new MethodSource(
            summary.Id,
            summary.QualifiedName,
            summary.FilePath,
            lineSpan.StartLinePosition.Line + 1,
            lineSpan.EndLinePosition.Line + 1,
            "C#",
            sourceText.Substring(method.Span.Start, method.Span.Length));
    }

    private static MethodSource? TryGetStructuredMethodSource(MethodSummary summary, string fullPath, string sourceText)
    {
        var parsed = StructuredSymbolReader.Parse(fullPath);
        var method = parsed.Types
            .FirstOrDefault(t => string.Equals(t.Name, summary.TypeName, StringComparison.Ordinal))
            ?.Methods.FirstOrDefault(m => string.Equals(m.Name, summary.Name, StringComparison.Ordinal));

        if (method is null)
        {
            return null;
        }

        var lines = sourceText.Replace("\r\n", "\n").Split('\n');
        var startIndex = method.StartLine - 1;
        var endIndex = Math.Min(method.EndLine, lines.Length);
        var snippet = startIndex >= 0 && startIndex < lines.Length
            ? string.Join('\n', lines[startIndex..endIndex])
            : string.Empty;

        return new MethodSource(
            summary.Id,
            summary.QualifiedName,
            summary.FilePath,
            method.StartLine,
            method.EndLine,
            StructuredSymbolReader.LanguageOf(fullPath),
            snippet);
    }

    private static Task<TypeSummary?> BuildTypeSummaryAsync((string Id, string Name, string Namespace, string FilePath, string Project, string Repository, string RootPath) row, TypeDeclarationSyntax typeNode, string sourceText, CancellationToken cancellationToken)
    {
        var lineSpan = typeNode.GetLocation().GetLineSpan();
        var ns = row.Namespace;
        var qualifiedName = string.IsNullOrWhiteSpace(ns) ? row.Name : $"{ns}.{row.Name}";
        var methods = typeNode.Members.OfType<MethodDeclarationSyntax>()
            .Select(m => new FileMethodSnapshot(Guid.NewGuid().ToString(), m.Identifier.ValueText, row.Name, ns))
            .ToList();
        var ctors = typeNode.Members.OfType<ConstructorDeclarationSyntax>()
            .Select(c => new FileMethodSnapshot(Guid.NewGuid().ToString(), c.Identifier.ValueText, row.Name, ns))
            .ToList();
        var properties = typeNode.Members.OfType<PropertyDeclarationSyntax>()
            .Select(p => new FilePropertySnapshot(Guid.NewGuid().ToString(), p.Identifier.ValueText, row.Name, ns))
            .ToList();
        var fields = typeNode.Members.OfType<FieldDeclarationSyntax>()
            .SelectMany(f => f.Declaration.Variables.Select(v => new FileFieldSnapshot(Guid.NewGuid().ToString(), v.Identifier.ValueText, row.Name, ns)))
            .ToList();

        TypeSummary summary = new(
            row.Id,
            row.Name,
            qualifiedName,
            typeNode.Kind().ToString().Replace("Declaration", "", StringComparison.OrdinalIgnoreCase),
            ns,
            row.Project,
            row.FilePath,
            null,
            [],
            typeNode.TypeParameterList?.Parameters.Select(p => p.Identifier.ValueText).ToList() ?? [],
            GetVisibility(typeNode.Modifiers),
            ctors,
            properties,
            fields,
            methods,
            [],
            sourceText.Split('\n').Length,
            lineSpan.StartLinePosition.Line + 1,
            lineSpan.EndLinePosition.Line + 1);
        return Task.FromResult<TypeSummary?>(summary);
    }

    private static TypeSummary BuildPythonTypeSummary(
        (string Id, string Name, string Namespace, string FilePath, string Project, string Repository, string RootPath) row,
        ParsedType type,
        string sourceText)
    {
        var methods = type.Methods
            .Where(method => !method.IsConstructor)
            .Select(method => new FileMethodSnapshot(Guid.NewGuid().ToString(), method.Name, type.Name, row.Namespace))
            .ToList();
        var constructors = type.Methods
            .Where(method => method.IsConstructor)
            .Select(method => new FileMethodSnapshot(Guid.NewGuid().ToString(), method.Name, type.Name, row.Namespace))
            .ToList();
        var properties = type.Properties
            .Select(property => new FilePropertySnapshot(Guid.NewGuid().ToString(), property.Name, type.Name, row.Namespace))
            .ToList();
        var fields = type.Fields
            .Select(field => new FileFieldSnapshot(Guid.NewGuid().ToString(), field.Name, type.Name, row.Namespace))
            .ToList();

        return new TypeSummary(
            row.Id,
            type.Name,
            string.IsNullOrWhiteSpace(row.Namespace) ? type.Name : $"{row.Namespace}.{type.Name}",
            type.Kind,
            row.Namespace,
            row.Project,
            row.FilePath,
            null,
            [],
            [],
            type.Visibility,
            constructors,
            properties,
            fields,
            methods,
            [],
            sourceText.Split('\n').Length,
            type.StartLine,
            type.EndLine);
    }

    public async Task<int> CountIndexedFilesAsync(RepositoryId repositoryId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM files f
            INNER JOIN projects p ON p.id = f.project_id
            WHERE p.repository_id = $repository_id;
            """;
        command.Parameters.AddWithValue("$repository_id", repositoryId.Value.ToString());
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    public async Task<Repository?> GetRepositoryByRootPathAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, root_path
            FROM repositories
            WHERE root_path = $root_path
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$root_path", rootPath);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new Repository(
            new RepositoryId(Guid.Parse(reader.GetString(0))),
            reader.GetString(1),
            reader.GetString(2));
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

    public async Task DeleteFileAsync(FileId fileId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM files WHERE id = $id;";
        command.Parameters.AddWithValue("$id", fileId.Value.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<Symbol>> FindSymbolsAsync(string columnName, string value, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT s.id, s.repository_id, s.project_id, s.file_id, s.container_id, s.kind, s.name, s.qualified_name, s.signature,
                   s.start_line, s.end_line, f.path
            FROM symbols s
            INNER JOIN files f ON f.id = s.file_id
            WHERE s.{columnName} = $value
            ORDER BY s.qualified_name;
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
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetInt32(9),
                reader.IsDBNull(10) ? null : reader.GetInt32(10),
                reader.GetString(11)));
        }

        return results;
    }

    private static async Task<IReadOnlyList<T>> QueryListAsync<T>(SqliteConnection connection, string commandText, string fileId, CancellationToken cancellationToken, Func<SqliteDataReader, T> map)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Parameters.AddWithValue("$file_id", fileId);
        var results = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(map(reader));
        }
        return results;
    }

    private static Task<int> CountLinesAsync(string rootPath, string relativePath, CancellationToken cancellationToken)
    {
        var fullPath = Path.Combine(rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!System.IO.File.Exists(fullPath))
        {
            return Task.FromResult(0);
        }

        return Task.FromResult(System.IO.File.ReadAllLines(fullPath).Length);
    }

    private static Task<IReadOnlyList<string>> GetProjectDependenciesAsync(string rootPath, string projectPath, CancellationToken cancellationToken)
    {
        var fullPath = Path.Combine(rootPath, projectPath.Replace('/', Path.DirectorySeparatorChar));
        if (!System.IO.File.Exists(fullPath))
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        var dependencies = new List<string>();
        using var stream = System.IO.File.OpenRead(fullPath);
        var document = XDocument.Load(stream);
        var projectReferences = document.Descendants().Where(element => element.Name.LocalName == "ProjectReference");
        foreach (var reference in projectReferences)
        {
            var include = reference.Attribute("Include")?.Value;
            if (!string.IsNullOrWhiteSpace(include))
            {
                dependencies.Add(include.Replace('\\', '/'));
            }
        }

        var packageReferences = document.Descendants().Where(element => element.Name.LocalName == "PackageReference");
        foreach (var reference in packageReferences)
        {
            var include = reference.Attribute("Include")?.Value;
            var version = reference.Attribute("Version")?.Value;
            if (!string.IsNullOrWhiteSpace(include))
            {
                dependencies.Add(string.IsNullOrWhiteSpace(version) ? include : $"{include}@{version}");
            }
        }

        return Task.FromResult<IReadOnlyList<string>>(dependencies);
    }

    private static string GetVisibility(SyntaxTokenList modifiers)
    {
        if (modifiers.Any(SyntaxKind.PublicKeyword)) return "public";
        if (modifiers.Any(SyntaxKind.PrivateKeyword)) return "private";
        if (modifiers.Any(SyntaxKind.ProtectedKeyword)) return "protected";
        if (modifiers.Any(SyntaxKind.InternalKeyword)) return "internal";
        return "private";
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

    public Task AddOperationMetricAsync(OperationMetric metric, CancellationToken cancellationToken = default)
        => InitializeSessionStoreAsync(cancellationToken).ContinueWith(_ => SessionStore().AddOperationMetricAsync(metric, cancellationToken), cancellationToken).Unwrap();

    public Task<IReadOnlyList<OperationMetric>> GetOperationMetricsAsync(SessionId sessionId, CancellationToken cancellationToken = default)
        => InitializeSessionStoreAsync(cancellationToken).ContinueWith(_ => SessionStore().GetOperationMetricsAsync(sessionId, cancellationToken), cancellationToken).Unwrap();

    private SqliteSessionStore SessionStore() => new(_connectionString);

    private async Task InitializeSessionStoreAsync(CancellationToken cancellationToken)
        => await SessionStore().InitializeAsync(cancellationToken);
}
