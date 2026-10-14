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

    /// <summary>Set when a history write was skipped because the database is read-only; the CLI prints it once.</summary>
    public static string? HistoryFailure { get; private set; }

    public static void ClearHistoryFailure() => HistoryFailure = null;

    public static bool IsReadOnlyFailure(SqliteException error) => error.SqliteErrorCode is 8 or 14;

    public async Task AddOperationMetricAsync(OperationMetric metric, CancellationToken cancellationToken = default)
    {
        try
        {
            await InitializeSessionStoreAsync(cancellationToken);
            await SessionStore().AddOperationMetricAsync(metric, cancellationToken);
        }
        catch (SqliteException error) when (IsReadOnlyFailure(error))
        {
            HistoryFailure = error.Message;
        }
    }

    public Task<IReadOnlyList<OperationMetric>> GetOperationMetricsAsync(SessionId sessionId, CancellationToken cancellationToken = default)
        => InitializeSessionStoreAsync(cancellationToken).ContinueWith(_ => SessionStore().GetOperationMetricsAsync(sessionId, cancellationToken), cancellationToken).Unwrap();

    private SqliteSessionStore SessionStore() => new(_connectionString);

    private async Task InitializeSessionStoreAsync(CancellationToken cancellationToken)
        => await SessionStore().InitializeAsync(cancellationToken);
}
