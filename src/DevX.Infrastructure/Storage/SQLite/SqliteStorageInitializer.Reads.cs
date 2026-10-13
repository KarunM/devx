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
    public async Task<IReadOnlyList<Symbol>> FindSymbolsByNameAsync(string name, CancellationToken cancellationToken = default)
        => await FindSymbolsAsync("name", name, cancellationToken);

    public async Task<Symbol?> FindReadContextTargetAsync(RepositoryId repositoryId, string filePath, string qualifiedName, CancellationToken cancellationToken = default)
    {
        var repositoryRoot = await GetRepositoryRootPathAsync(repositoryId, cancellationToken);
        if (string.IsNullOrWhiteSpace(repositoryRoot)) return null;
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id, s.repository_id, s.project_id, s.file_id, s.container_id, s.kind, s.name, s.qualified_name,
                   s.signature, s.start_line, s.end_line, f.path
            FROM symbols s
            INNER JOIN files f ON f.id = s.file_id
            INNER JOIN repositories r ON r.id = s.repository_id
            WHERE r.root_path = $root_path AND f.path = $file_path
              AND (s.qualified_name = $qualified_name OR s.name = $name)
              AND (s.kind = 'Method' OR s.kind = 'Function')
            ORDER BY CASE WHEN s.qualified_name = $qualified_name THEN 0 ELSE 1 END, s.start_line, s.id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$root_path", Path.GetFullPath(repositoryRoot));
        command.Parameters.AddWithValue("$file_path", filePath);
        command.Parameters.AddWithValue("$qualified_name", qualifiedName);
        command.Parameters.AddWithValue("$name", qualifiedName.Split('.').Last());
        var rows = await ReadSymbolsAsync(command, cancellationToken);
        return rows.FirstOrDefault();
    }

    public async Task<ReadContextCandidates> FindReadContextCandidatesAsync(Symbol target, IReadOnlyList<string> sourceNames, bool sourceNamesTruncated, int limit = 5, CancellationToken cancellationToken = default)
    {
        var boundedLimit = Math.Clamp(limit, 1, 20);
        var boundedNames = sourceNames.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal).Take(64).ToList();
        var tests = await ReadTestContextSymbolsAsync(target, boundedLimit, cancellationToken);
        var sameFileMatches = boundedNames.Count == 0
            ? []
            : await ReadSameFileContextSymbolsAsync(target, boundedNames, boundedLimit, cancellationToken);
        return new ReadContextCandidates(
            tests.Take(boundedLimit).ToList(),
            tests.Count > boundedLimit,
            sameFileMatches.Take(boundedLimit).ToList(),
            sameFileMatches.Count > boundedLimit,
            sameFileMatches.Count > 0,
            sourceNamesTruncated || sourceNames.Count > boundedNames.Count);
    }

    private async Task<List<Symbol>> ReadTestContextSymbolsAsync(Symbol target, int limit, CancellationToken cancellationToken)
    {
        var targetModule = Path.GetFileNameWithoutExtension(target.FilePath);
        if (string.IsNullOrWhiteSpace(targetModule)) return [];
        var escapedModule = targetModule.Replace("!", "!!", StringComparison.Ordinal)
            .Replace("%", "!%", StringComparison.Ordinal)
            .Replace("_", "!_", StringComparison.Ordinal);
        var testPathPatterns = new[]
        {
            $"%/test_{escapedModule}.%", $"test_{escapedModule}.%",
            $"%/{escapedModule}!_test.%", $"{escapedModule}!_test.%",
        };
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                SELECT s.id, s.repository_id, s.project_id, s.file_id, s.container_id, s.kind, s.name, s.qualified_name,
                       s.signature, s.start_line, s.end_line, f.path
                FROM symbols s
                INNER JOIN files f ON f.id = s.file_id
                WHERE s.repository_id = $repository_id
                  AND s.id <> $target_id
                  AND (s.kind = 'Method' OR s.kind = 'Function')
                  AND lower(s.name) = lower($exact_test_name)
                  AND (
                    lower(f.path) LIKE lower($test_path_0) ESCAPE '!' OR lower(f.path) LIKE lower($test_path_1) ESCAPE '!' OR
                    lower(f.path) LIKE lower($test_path_2) ESCAPE '!' OR lower(f.path) LIKE lower($test_path_3) ESCAPE '!'
                  )
                ORDER BY s.qualified_name, f.path, s.start_line
                LIMIT $limit;
                """
            ;
        command.Parameters.AddWithValue("$repository_id", target.RepositoryId);
        command.Parameters.AddWithValue("$target_id", target.Id);
        command.Parameters.AddWithValue("$exact_test_name", $"test_{target.Name}");
        for (var index = 0; index < testPathPatterns.Length; index++)
            command.Parameters.AddWithValue($"$test_path_{index}", testPathPatterns[index]);
        command.Parameters.AddWithValue("$limit", limit + 1);
        return await ReadSymbolsAsync(command, cancellationToken);
    }

    private async Task<List<Symbol>> ReadSameFileContextSymbolsAsync(Symbol target, IReadOnlyList<string> names, int limit, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        if (names.Count == 0)
        {
            command.CommandText = """
                SELECT s.id, s.repository_id, s.project_id, s.file_id, s.container_id, s.kind, s.name, s.qualified_name,
                       s.signature, s.start_line, s.end_line, f.path
                FROM symbols s
                INNER JOIN files f ON f.id = s.file_id
                WHERE s.repository_id = $repository_id
                  AND s.file_id = $file_id
                  AND s.id <> $target_id
                  AND (s.kind = 'Method' OR s.kind = 'Function')
                ORDER BY s.start_line, s.qualified_name
                LIMIT $limit;
                """;
        }
        else
        {
            var nameParameters = names.Select((_, index) => $"$name{index}").ToArray();
            var orderCases = string.Join(" ", names.Select((_, index) => $"WHEN $name{index} THEN {index}"));
            command.CommandText = $"""
                SELECT s.id, s.repository_id, s.project_id, s.file_id, s.container_id, s.kind, s.name, s.qualified_name,
                       s.signature, s.start_line, s.end_line, f.path
                FROM symbols s
                INNER JOIN files f ON f.id = s.file_id
                WHERE s.repository_id = $repository_id
                  AND s.file_id = $file_id
                  AND s.id <> $target_id
                  AND (s.kind = 'Method' OR s.kind = 'Function')
                  AND s.name IN ({string.Join(", ", nameParameters)})
                ORDER BY CASE s.name {orderCases} ELSE {names.Count} END, s.start_line, s.qualified_name
                LIMIT $limit;
                """;
            for (var index = 0; index < names.Count; index++)
            {
                command.Parameters.AddWithValue(nameParameters[index], names[index]);
            }
        }
        command.Parameters.AddWithValue("$repository_id", target.RepositoryId);
        command.Parameters.AddWithValue("$target_id", target.Id);
        command.Parameters.AddWithValue("$limit", limit + 1);
        command.Parameters.AddWithValue("$file_id", target.FileId);
        return await ReadSymbolsAsync(command, cancellationToken);
    }

    private static async Task<List<Symbol>> ReadSymbolsAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var results = new List<Symbol>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new Symbol(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetString(6),
                reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetInt32(9), reader.IsDBNull(10) ? null : reader.GetInt32(10),
                reader.GetString(11)));
        }

        return results;
    }

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

    /// <summary>The innermost indexed symbol whose span contains each point, keyed by
    /// (file id, line). `devx search text` prints this beside every hit: which function or type
    /// the matching line sits in is the one thing ripgrep cannot say, and without it the next
    /// call is always a read.</summary>
    public async Task<IReadOnlyDictionary<(string FileId, int Line), string>> FindEnclosingSymbolsAsync(
        IReadOnlyList<(string FileId, int Line)> points,
        CancellationToken cancellationToken = default)
    {
        var enclosing = new Dictionary<(string FileId, int Line), string>();
        if (points.Count == 0)
        {
            return enclosing;
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var spans = new Dictionary<string, List<(string Name, int Start, int End)>>(StringComparer.Ordinal);
        foreach (var fileId in points.Select(point => point.FileId).Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT qualified_name, start_line, end_line
                FROM symbols
                WHERE file_id = $fileId AND start_line IS NOT NULL AND end_line IS NOT NULL;
                """;
            command.Parameters.AddWithValue("$fileId", fileId);
            var list = new List<(string, int, int)>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                list.Add((reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2)));
            }

            spans[fileId] = list;
        }

        foreach (var (fileId, line) in points)
        {
            if (!spans.TryGetValue(fileId, out var candidates))
            {
                continue;
            }

            string? best = null;
            var bestSpan = int.MaxValue;
            var bestStart = 0;
            foreach (var (name, start, end) in candidates)
            {
                if (line < start || line > end)
                {
                    continue;
                }

                var span = end - start;
                if (span < bestSpan || (span == bestSpan && start > bestStart))
                {
                    best = name;
                    bestSpan = span;
                    bestStart = start;
                }
            }

            if (best is not null)
            {
                enclosing[(fileId, line)] = best;
            }
        }

        return enclosing;
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
            SELECT t.id, t.name, n.name, s.start_line, s.end_line
            FROM types t
            INNER JOIN namespaces n ON n.id = t.namespace_id
            LEFT JOIN symbols s ON s.file_id = n.file_id AND s.container_id = n.id AND s.name = t.name
            WHERE n.file_id = $file_id
            ORDER BY n.name, t.name;
            """, fileId, cancellationToken, row => new FileTypeSnapshot(row.GetString(0), row.GetString(1), row.GetString(2), row.IsDBNull(3) ? null : row.GetInt32(3), row.IsDBNull(4) ? null : row.GetInt32(4)));

        var methods = await QueryListAsync(connection, """
            SELECT m.id, m.name, t.name, n.name,
                   (SELECT s.signature FROM symbols s WHERE s.container_id = t.id AND s.name = m.name ORDER BY s.start_line LIMIT 1),
                   (SELECT s.start_line FROM symbols s WHERE s.container_id = t.id AND s.name = m.name ORDER BY s.start_line LIMIT 1),
                   (SELECT s.end_line FROM symbols s WHERE s.container_id = t.id AND s.name = m.name ORDER BY s.start_line LIMIT 1)
            FROM methods m
            INNER JOIN types t ON t.id = m.type_id
            INNER JOIN namespaces n ON n.id = t.namespace_id
            WHERE n.file_id = $file_id
            ORDER BY n.name, t.name, m.name;
            """, fileId, cancellationToken, row => new FileMethodSnapshot(row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3), row.IsDBNull(4) ? null : row.GetString(4), row.IsDBNull(5) ? null : row.GetInt32(5), row.IsDBNull(6) ? null : row.GetInt32(6)));

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

    public async Task<FileSnapshot?> GetFileSnapshotAsync(RepositoryId repositoryId, string repositoryRoot, string relativePath, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var rootMatch = OperatingSystem.IsWindows() ? "lower(r.root_path) = lower($root)" : "r.root_path = $root";
        var pathMatch = OperatingSystem.IsWindows() ? "lower(f.path) = lower($path)" : "f.path = $path";
        command.CommandText = $"""
            SELECT f.id
            FROM files f
            INNER JOIN projects p ON p.id = f.project_id
            INNER JOIN repositories r ON r.id = p.repository_id
            WHERE {rootMatch} AND {pathMatch}
            ORDER BY CASE WHEN r.id = $repository_id THEN 0 ELSE 1 END, r.id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$repository_id", repositoryId.Value.ToString());
        command.Parameters.AddWithValue("$root", Path.GetFullPath(repositoryRoot));
        command.Parameters.AddWithValue("$path", relativePath.Replace('\\', '/'));
        var fileId = await command.ExecuteScalarAsync(cancellationToken);
        return fileId is string id ? await GetFileSnapshotAsync(id, cancellationToken) : null;
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

    /// <summary>Every method/constructor of one repository with its signature, span and location: the input for the overload-aware call scan.</summary>
    public async Task<IReadOnlyList<MethodCandidate>> ListMethodCandidatesAsync(string repositoryId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id, s.repository_id, s.project_id, s.file_id, s.container_id, s.kind, s.name, s.qualified_name, s.signature,
                   s.start_line, s.end_line, f.path, n.name, p.name, t.name, r.root_path
            FROM symbols s
            INNER JOIN files f ON f.id = s.file_id
            INNER JOIN projects p ON p.id = f.project_id
            INNER JOIN repositories r ON r.id = p.repository_id
            INNER JOIN types t ON t.id = s.container_id
            INNER JOIN namespaces n ON n.id = t.namespace_id
            WHERE s.kind IN ('Method', 'Constructor') AND s.repository_id = $repository_id
            ORDER BY s.qualified_name, f.path, s.start_line;
            """;
        command.Parameters.AddWithValue("$repository_id", repositoryId);
        var results = new List<MethodCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var symbol = new Symbol(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetInt32(9), reader.IsDBNull(10) ? null : reader.GetInt32(10), reader.GetString(11));
            results.Add(new MethodCandidate(symbol, reader.GetString(12), reader.GetString(13), reader.GetString(14), reader.GetString(15)));
        }
        return results;
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
}
