using System.Diagnostics;
using DevX.Domain;
using DevX.Infrastructure.Storage.SQLite;
using Microsoft.Data.Sqlite;
using IOFile = System.IO.File;

namespace DevX.Cli;

/// <summary>
/// Each worktree keeps its own index database, so a session created in the main checkout is unknown
/// in a git worktree of the same repository. Worktrees are found through git (all of them share one
/// common git directory); a session that exists in a sibling worktree database is adopted into this
/// one, bound to THIS worktree's repository root so its files and paths are indexed here.
/// </summary>
public static class SessionFederation
{
    public static async Task<bool> EnsureSessionAsync(SqliteStorageInitializer store, string localDatabasePath, string workingDirectory, string repositoryRoot, string sessionId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(sessionId, out var id)) return false;
        if (await store.GetSessionAsync(new GetSessionRequest(new SessionId(id)), cancellationToken) is not null) return true;

        var local = Path.GetFullPath(localDatabasePath);
        foreach (var worktree in await ListWorktreesAsync(workingDirectory, cancellationToken))
        {
            var candidate = Path.GetFullPath(Path.Combine(worktree, ".devx", "devx.db"));
            if (string.Equals(candidate, local, StringComparison.Ordinal) || !IOFile.Exists(candidate)) continue;
            if (!await HasSessionAsync(candidate, id, cancellationToken)) continue;

            var repository = new Repository(new RepositoryId(StableId(repositoryRoot)), Path.GetFileName(repositoryRoot), repositoryRoot);
            await store.UpsertRepositoryAsync(repository, cancellationToken);
            await AdoptAsync(local, id, repository.Id, cancellationToken);
            return true;
        }

        return false;
    }

    private static async Task<bool> HasSessionAsync(string databasePath, Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM sessions WHERE id = $id LIMIT 1;";
            command.Parameters.AddWithValue("$id", id.ToString());
            return await command.ExecuteScalarAsync(cancellationToken) is not null;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static async Task AdoptAsync(string localDatabasePath, Guid id, RepositoryId repositoryId, CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = localDatabasePath, Pooling = false }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO sessions (id, repository_id, working_set, search_history, expanded_files, expanded_symbols, diagnostics_history, patch_history, build_test_history)
            VALUES ($id, $repository_id, NULL, '[]', '[]', '[]', '[]', '[]', '[]');
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$repository_id", repositoryId.Value.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<string>> ListWorktreesAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo("git", "worktree list --porcelain")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(startInfo);
            if (process is null) return [];
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0) return [];
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r'))
                .Where(line => line.StartsWith("worktree ", StringComparison.Ordinal))
                .Select(line => line["worktree ".Length..])
                .ToArray();
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return [];
        }
    }

    private static Guid StableId(string value)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
        return new Guid(hash.AsSpan(0, 16));
    }
}
