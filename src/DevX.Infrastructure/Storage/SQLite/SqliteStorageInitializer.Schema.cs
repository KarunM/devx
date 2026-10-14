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
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        // CREATE IF NOT EXISTS and the column checks below still require a write lock on
        // SQLite. Avoid running schema DDL at all when this database already has the current schema.
        if (await IsSchemaCurrentAsync(connection, cancellationToken)) return;

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS repositories (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                root_path TEXT NOT NULL UNIQUE
            );

            CREATE TABLE IF NOT EXISTS dirty_index_repositories (
                root_path TEXT PRIMARY KEY,
                requires_full_reindex INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS dirty_index_files (
                root_path TEXT NOT NULL,
                file_path TEXT NOT NULL,
                PRIMARY KEY (root_path, file_path),
                FOREIGN KEY (root_path) REFERENCES dirty_index_repositories(root_path) ON DELETE CASCADE
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

    private static async Task<bool> IsSchemaCurrentAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM sqlite_master
            WHERE type = 'table' AND name IN (
                'repositories', 'dirty_index_repositories', 'dirty_index_files', 'projects', 'files',
                'namespaces', 'file_index_observations', 'types', 'methods', 'properties', 'fields',
                'symbols', 'references_table', 'diagnostics', 'tests', 'sessions'
            );
            """;
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) != 16) return false;

        return await HasColumnsAsync(connection, "references_table", ["caller_symbol_id", "call_site_line"], cancellationToken)
            && await HasColumnsAsync(connection, "symbols", ["start_line", "end_line"], cancellationToken);
    }

    private static async Task<bool> HasColumnsAsync(SqliteConnection connection, string table, string[] required, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) columns.Add(reader.GetString(1));
        return required.All(columns.Contains);
    }

    public async Task<IReadOnlyList<DirtyIndexRepository>> GetDirtyIndexRepositoriesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.root_path, r.requires_full_reindex, f.file_path
            FROM dirty_index_repositories r
            LEFT JOIN dirty_index_files f ON f.root_path = r.root_path
            ORDER BY r.root_path, f.file_path;
            """;
        var states = new List<DirtyIndexRepository>();
        var byRoot = new Dictionary<string, (bool Full, List<string> Files)>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var root = reader.GetString(0);
            if (!byRoot.TryGetValue(root, out var state))
            {
                state = (reader.GetInt64(1) != 0, []);
                byRoot[root] = state;
            }
            if (!reader.IsDBNull(2)) state.Files.Add(reader.GetString(2));
        }
        states.AddRange(byRoot.Select(pair => new DirtyIndexRepository(pair.Key, pair.Value.Full, pair.Value.Files)));
        return states;
    }

    public async Task<DirtyIndexRepository?> GetDirtyIndexRepositoryAsync(string repositoryRoot, CancellationToken cancellationToken = default)
        => (await GetDirtyIndexRepositoriesAsync(cancellationToken))
            .FirstOrDefault(state => PathsEqual(state.RepositoryRoot, NormalizeRepositoryRoot(repositoryRoot)));

    public async Task<bool> TryMarkIndexFilesDirtyAsync(string repositoryRoot, IReadOnlyList<string> files, CancellationToken cancellationToken = default)
    {
        var root = NormalizeRepositoryRoot(repositoryRoot);
        var normalizedFiles = NormalizeDirtyFiles(files, root);
        if (normalizedFiles.Length == 0) return false;

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var check = connection.CreateCommand())
        {
            check.Transaction = (SqliteTransaction)transaction;
            check.CommandText = "SELECT 1 FROM dirty_index_repositories WHERE root_path = $root LIMIT 1;";
            check.Parameters.AddWithValue("$root", root);
            if (await check.ExecuteScalarAsync(cancellationToken) is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }

        await using (var insertRoot = connection.CreateCommand())
        {
            insertRoot.Transaction = (SqliteTransaction)transaction;
            insertRoot.CommandText = "INSERT INTO dirty_index_repositories(root_path, requires_full_reindex) VALUES($root, 0);";
            insertRoot.Parameters.AddWithValue("$root", root);
            await insertRoot.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var file in normalizedFiles)
        {
            await using var insertFile = connection.CreateCommand();
            insertFile.Transaction = (SqliteTransaction)transaction;
            insertFile.CommandText = "INSERT INTO dirty_index_files(root_path, file_path) VALUES($root, $file);";
            insertFile.Parameters.AddWithValue("$root", root);
            insertFile.Parameters.AddWithValue("$file", file);
            await insertFile.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task MarkIndexRepositoryDirtyAsync(string repositoryRoot, IReadOnlyList<string> files, CancellationToken cancellationToken = default)
    {
        var root = NormalizeRepositoryRoot(repositoryRoot);
        var normalizedFiles = NormalizeDirtyFiles(files, root);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var upsertRoot = connection.CreateCommand())
        {
            upsertRoot.Transaction = (SqliteTransaction)transaction;
            upsertRoot.CommandText = """
                INSERT INTO dirty_index_repositories(root_path, requires_full_reindex) VALUES($root, 1)
                ON CONFLICT(root_path) DO UPDATE SET requires_full_reindex = 1;
                """;
            upsertRoot.Parameters.AddWithValue("$root", root);
            await upsertRoot.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var file in normalizedFiles)
        {
            await using var insertFile = connection.CreateCommand();
            insertFile.Transaction = (SqliteTransaction)transaction;
            insertFile.CommandText = "INSERT OR IGNORE INTO dirty_index_files(root_path, file_path) VALUES($root, $file);";
            insertFile.Parameters.AddWithValue("$root", root);
            insertFile.Parameters.AddWithValue("$file", file);
            await insertFile.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MarkIndexFilesDirtyAsync(string repositoryRoot, IReadOnlyList<string> files, CancellationToken cancellationToken = default)
    {
        var root = NormalizeRepositoryRoot(repositoryRoot);
        var normalizedFiles = NormalizeDirtyFiles(files, root);
        if (normalizedFiles.Length == 0) return;
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var insertRoot = connection.CreateCommand())
        {
            insertRoot.Transaction = (SqliteTransaction)transaction;
            insertRoot.CommandText = "INSERT OR IGNORE INTO dirty_index_repositories(root_path, requires_full_reindex) VALUES($root, 0);";
            insertRoot.Parameters.AddWithValue("$root", root);
            await insertRoot.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var file in normalizedFiles)
        {
            await using var insertFile = connection.CreateCommand();
            insertFile.Transaction = (SqliteTransaction)transaction;
            insertFile.CommandText = "INSERT OR IGNORE INTO dirty_index_files(root_path, file_path) VALUES($root, $file);";
            insertFile.Parameters.AddWithValue("$root", root);
            insertFile.Parameters.AddWithValue("$file", file);
            await insertFile.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ClearDirtyIndexFilesAsync(string repositoryRoot, IReadOnlyList<string> files, CancellationToken cancellationToken = default)
    {
        var root = NormalizeRepositoryRoot(repositoryRoot);
        var normalizedFiles = NormalizeDirtyFiles(files, root);
        if (normalizedFiles.Length == 0) return;
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var check = connection.CreateCommand())
        {
            check.Transaction = (SqliteTransaction)transaction;
            check.CommandText = "SELECT requires_full_reindex FROM dirty_index_repositories WHERE root_path = $root;";
            check.Parameters.AddWithValue("$root", root);
            if (Convert.ToInt64(await check.ExecuteScalarAsync(cancellationToken) ?? 1) == 0)
            {
                foreach (var file in normalizedFiles)
                {
                    await using var deleteFile = connection.CreateCommand();
                    deleteFile.Transaction = (SqliteTransaction)transaction;
                    deleteFile.CommandText = "DELETE FROM dirty_index_files WHERE root_path = $root AND file_path = $file;";
                    deleteFile.Parameters.AddWithValue("$root", root);
                    deleteFile.Parameters.AddWithValue("$file", file);
                    await deleteFile.ExecuteNonQueryAsync(cancellationToken);
                }
                await using var deleteRoot = connection.CreateCommand();
                deleteRoot.Transaction = (SqliteTransaction)transaction;
                deleteRoot.CommandText = """
                    DELETE FROM dirty_index_repositories
                    WHERE root_path = $root AND requires_full_reindex = 0
                      AND NOT EXISTS (SELECT 1 FROM dirty_index_files WHERE root_path = $root);
                    """;
                deleteRoot.Parameters.AddWithValue("$root", root);
                await deleteRoot.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ClearDirtyIndexRepositoryAsync(string repositoryRoot, CancellationToken cancellationToken = default)
    {
        var root = NormalizeRepositoryRoot(repositoryRoot);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var deleteFiles = connection.CreateCommand())
        {
            deleteFiles.Transaction = (SqliteTransaction)transaction;
            deleteFiles.CommandText = "DELETE FROM dirty_index_files WHERE root_path = $root;";
            deleteFiles.Parameters.AddWithValue("$root", root);
            await deleteFiles.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var deleteRoot = connection.CreateCommand())
        {
            deleteRoot.Transaction = (SqliteTransaction)transaction;
            deleteRoot.CommandText = "DELETE FROM dirty_index_repositories WHERE root_path = $root;";
            deleteRoot.Parameters.AddWithValue("$root", root);
            await deleteRoot.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static string NormalizeRepositoryRoot(string repositoryRoot)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));

    private static string[] NormalizeDirtyFiles(IReadOnlyList<string> files, string root)
        => files.Where(file => !string.IsNullOrWhiteSpace(file))
            .Select(file => NormalizeDirtyFile(file, root))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray();

    private static string NormalizeDirtyFile(string file, string root)
    {
        if (Path.IsPathRooted(file))
        {
            var fullRoot = Path.GetFullPath(root);
            var fullPath = Path.GetFullPath(file);
            var relative = Path.GetRelativePath(fullRoot, fullPath);
            if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return relative.Replace('\\', '/');
        }
        return file.Replace('\\', '/').TrimStart('/');
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

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
}
