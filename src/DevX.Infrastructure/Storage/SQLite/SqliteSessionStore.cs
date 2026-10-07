using System.Text.Json;
using DevX.Domain;
using Microsoft.Data.Sqlite;

namespace DevX.Infrastructure.Storage.SQLite;

public sealed class SqliteSessionStore : ISessionStore
{
    private readonly string _connectionString;

    public SqliteSessionStore(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys = ON;
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

            CREATE TABLE IF NOT EXISTS operation_metrics (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NULL,
                operation TEXT NOT NULL,
                timestamp TEXT NOT NULL,
                duration_ticks INTEGER NOT NULL,
                success INTEGER NOT NULL,
                metadata_json TEXT NULL,
                agent_label TEXT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);

        await EnsureOperationMetricColumnsAsync(connection, cancellationToken);
    }

    private static async Task EnsureOperationMetricColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(operation_metrics);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString(1));
        }

        if (!columns.Contains("metadata_json"))
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE operation_metrics ADD COLUMN metadata_json TEXT NULL;";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!columns.Contains("agent_label"))
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE operation_metrics ADD COLUMN agent_label TEXT NULL;";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<SessionState> CreateSessionAsync(CreateSessionRequest request, CancellationToken cancellationToken = default)
    {
        var session = new SessionState(new SessionId(Guid.NewGuid()), request.RepositoryId, null, [], [], [], [], [], []);
        await SaveAsync(session, cancellationToken);
        return session;
    }

    public Task<SessionState?> GetSessionAsync(GetSessionRequest request, CancellationToken cancellationToken = default)
        => GetAsync(request.SessionId, cancellationToken);

    public async Task SaveSearchAsync(SaveSearchRequest request, CancellationToken cancellationToken = default)
    {
        var session = await RequireSessionAsync(request.SessionId, cancellationToken);
        var updated = session with { SearchHistory = session.SearchHistory.Concat([request.Query]).ToList() };
        await SaveAsync(updated, cancellationToken);
    }

    public async Task AddWorkingSetItemAsync(AddWorkingSetItemRequest request, CancellationToken cancellationToken = default)
    {
        var session = await RequireSessionAsync(request.SessionId, cancellationToken);
        var workingSet = string.IsNullOrWhiteSpace(session.WorkingSet) ? request.Item : $"{session.WorkingSet}\n{request.Item}";
        await SaveAsync(session with { WorkingSet = workingSet }, cancellationToken);
    }

    public async Task MarkSymbolExpandedAsync(MarkSymbolExpandedRequest request, CancellationToken cancellationToken = default)
    {
        var session = await RequireSessionAsync(request.SessionId, cancellationToken);
        await SaveAsync(session with { ExpandedSymbols = session.ExpandedSymbols.Concat([request.SymbolId]).Distinct().ToList() }, cancellationToken);
    }

    public async Task MarkFileExpandedAsync(MarkFileExpandedRequest request, CancellationToken cancellationToken = default)
    {
        var session = await RequireSessionAsync(request.SessionId, cancellationToken);
        await SaveAsync(session with { ExpandedFiles = session.ExpandedFiles.Concat([request.FileId]).Distinct().ToList() }, cancellationToken);
    }

    public async Task AddDiagnosticAsync(AddDiagnosticRequest request, CancellationToken cancellationToken = default)
    {
        var session = await RequireSessionAsync(request.SessionId, cancellationToken);
        var diagnostics = session.DiagnosticsHistory.Concat([JsonSerializer.Serialize(request.Diagnostic)]).ToList();
        await SaveAsync(session with { DiagnosticsHistory = diagnostics }, cancellationToken);
    }

    public async Task AddExecutionAsync(AddExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var session = await RequireSessionAsync(request.SessionId, cancellationToken);
        var buildHistory = session.BuildTestHistory.Concat([JsonSerializer.Serialize(request.Execution)]).ToList();
        await SaveAsync(session with { BuildTestHistory = buildHistory }, cancellationToken);
    }

    public async Task AddOperationMetricAsync(OperationMetric metric, CancellationToken cancellationToken = default)
    {
        if (OperationContext.TaskLabel.Value is { } task)
        {
            var metadata = System.Text.Json.Nodes.JsonNode.Parse(metric.MetadataJson ?? "{}")!.AsObject();
            metadata["TaskLabel"] = task;
            metric = metric with { MetadataJson = metadata.ToJsonString() };
        }
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO operation_metrics (session_id, operation, timestamp, duration_ticks, success, metadata_json, agent_label)
            VALUES ($session_id, $operation, $timestamp, $duration_ticks, $success, $metadata_json, $agent_label);
            """;
        command.Parameters.AddWithValue("$session_id", (object?)metric.SessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$operation", metric.Operation);
        command.Parameters.AddWithValue("$timestamp", metric.Timestamp.ToString("O"));
        command.Parameters.AddWithValue("$duration_ticks", metric.Duration.Ticks);
        command.Parameters.AddWithValue("$success", metric.Success ? 1 : 0);
        command.Parameters.AddWithValue("$metadata_json", (object?)metric.MetadataJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$agent_label", (object?)metric.AgentLabel ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OperationMetric>> GetOperationMetricsAsync(SessionId sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT operation, timestamp, duration_ticks, success, session_id, metadata_json, agent_label
            FROM operation_metrics
            WHERE session_id = $session_id
            ORDER BY id;
            """;
        command.Parameters.AddWithValue("$session_id", sessionId.Value.ToString());
        var results = new List<OperationMetric>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new OperationMetric(
                reader.GetString(0),
                DateTimeOffset.Parse(reader.GetString(1)),
                TimeSpan.FromTicks(reader.GetInt64(2)),
                reader.GetInt64(3) == 1,
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return results;
    }

    private async Task<SessionState> RequireSessionAsync(SessionId sessionId, CancellationToken cancellationToken)
        => await GetAsync(sessionId, cancellationToken) ?? throw new InvalidOperationException("Session not found.");

    private async Task SaveAsync(SessionState session, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sessions (id, repository_id, working_set, search_history, expanded_files, expanded_symbols, diagnostics_history, patch_history, build_test_history)
            VALUES ($id, $repository_id, $working_set, $search_history, $expanded_files, $expanded_symbols, $diagnostics_history, $patch_history, $build_test_history)
            ON CONFLICT(id) DO UPDATE SET
                repository_id = excluded.repository_id,
                working_set = excluded.working_set,
                search_history = excluded.search_history,
                expanded_files = excluded.expanded_files,
                expanded_symbols = excluded.expanded_symbols,
                diagnostics_history = excluded.diagnostics_history,
                patch_history = excluded.patch_history,
                build_test_history = excluded.build_test_history;
            """;
        command.Parameters.AddWithValue("$id", session.Id.Value.ToString());
        command.Parameters.AddWithValue("$repository_id", session.RepositoryId.Value.ToString());
        command.Parameters.AddWithValue("$working_set", (object?)session.WorkingSet ?? DBNull.Value);
        command.Parameters.AddWithValue("$search_history", JsonSerializer.Serialize(session.SearchHistory));
        command.Parameters.AddWithValue("$expanded_files", JsonSerializer.Serialize(session.ExpandedFiles));
        command.Parameters.AddWithValue("$expanded_symbols", JsonSerializer.Serialize(session.ExpandedSymbols));
        command.Parameters.AddWithValue("$diagnostics_history", JsonSerializer.Serialize(session.DiagnosticsHistory));
        command.Parameters.AddWithValue("$patch_history", JsonSerializer.Serialize(session.PatchHistory));
        command.Parameters.AddWithValue("$build_test_history", JsonSerializer.Serialize(session.BuildTestHistory));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SessionState?> GetAsync(SessionId sessionId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, repository_id, working_set, search_history, expanded_files, expanded_symbols, diagnostics_history, patch_history, build_test_history
            FROM sessions
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", sessionId.Value.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new SessionState(
            new SessionId(Guid.Parse(reader.GetString(0))),
            new RepositoryId(Guid.Parse(reader.GetString(1))),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            JsonSerializer.Deserialize<List<string>>(reader.GetString(3)) ?? [],
            JsonSerializer.Deserialize<List<string>>(reader.GetString(4)) ?? [],
            JsonSerializer.Deserialize<List<string>>(reader.GetString(5)) ?? [],
            JsonSerializer.Deserialize<List<string>>(reader.GetString(6)) ?? [],
            JsonSerializer.Deserialize<List<string>>(reader.GetString(7)) ?? [],
            JsonSerializer.Deserialize<List<string>>(reader.GetString(8)) ?? []);
    }
}
