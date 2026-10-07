using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DevX.Cli;

public static class IndexHealthCollector
{
    public static async Task<IndexHealthSnapshot> CollectAsync(string connectionString, string root)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.path, o.indexed_at FROM files f
            JOIN projects p ON p.id=f.project_id JOIN repositories r ON r.id=p.repository_id
            LEFT JOIN file_index_observations o ON o.file_id=f.id WHERE r.root_path=$root ORDER BY f.path;
            """;
        command.Parameters.AddWithValue("$root", Path.GetFullPath(root));
        var missing = new List<string>(); var stale = new List<string>(); var unknown = 0; var known = 0;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var path = reader.GetString(0); var absolute = Path.Combine(root, path);
            if (!File.Exists(absolute)) { missing.Add(path); continue; }
            if (reader.IsDBNull(1)) { unknown++; continue; }
            known++;
            var indexed = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture);
            if (File.GetLastWriteTimeUtc(absolute) > indexed.UtcDateTime) stale.Add(path);
        }
        return new(Path.GetFullPath(root), DateTimeOffset.UtcNow, missing.Count, unknown > 0 && known == 0 ? null : stale.Count,
            unknown, missing.Take(10).ToList(), stale.Take(10).ToList());
    }
}
