using DevX.Domain;

namespace DevX.Infrastructure.Storage.SQLite;

public sealed partial class SqliteStorageInitializer : IRepositoryStore, IRootScopedFileSnapshotStore, ISymbolStore, IReadContextStore, ISessionStore, IIndexFreshnessStore, IContentSearchCoverageStore
{
    private readonly string _connectionString;

    public SqliteStorageInitializer(string connectionString)
    {
        _connectionString = connectionString;
    }
}
