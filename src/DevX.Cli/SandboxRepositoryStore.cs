using DevX.Domain;
using DomainFile = DevX.Domain.File;

namespace DevX.Cli;

/// <summary>
/// Wraps an IRepositoryStore to override the repository root path for sandbox mode.
/// All operations delegate to the inner store except GetRepositoryRootPathAsync.
/// </summary>
public sealed class SandboxRepositoryStore : IRepositoryStore, IRootScopedFileSnapshotStore, ISymbolStore, IReadContextStore, ISessionStore, IIndexFreshnessStore, IContentSearchCoverageStore
{
    private readonly IRepositoryStore _innerStore;
    private readonly string _sandboxRoot;

    public SandboxRepositoryStore(IRepositoryStore innerStore, string sandboxRoot)
    {
        _innerStore = innerStore;
        _sandboxRoot = sandboxRoot;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
        => _innerStore.InitializeAsync(cancellationToken);

    public Task UpsertRepositoryAsync(Repository repository, CancellationToken cancellationToken = default)
        => _innerStore.UpsertRepositoryAsync(repository, cancellationToken);

    public Task UpsertProjectAsync(Project project, CancellationToken cancellationToken = default)
        => _innerStore.UpsertProjectAsync(project, cancellationToken);

    public Task UpsertFileAsync(DomainFile file, CancellationToken cancellationToken = default)
        => _innerStore.UpsertFileAsync(file, cancellationToken);

    public Task UpsertNamespaceAsync(Namespace @namespace, CancellationToken cancellationToken = default)
        => _innerStore.UpsertNamespaceAsync(@namespace, cancellationToken);

    public Task UpsertTypeAsync(Domain.Type type, CancellationToken cancellationToken = default)
        => _innerStore.UpsertTypeAsync(type, cancellationToken);

    public Task UpsertMethodAsync(Method method, CancellationToken cancellationToken = default)
        => _innerStore.UpsertMethodAsync(method, cancellationToken);

    public Task UpsertPropertyAsync(Property property, CancellationToken cancellationToken = default)
        => _innerStore.UpsertPropertyAsync(property, cancellationToken);

    public Task UpsertFieldAsync(Field field, CancellationToken cancellationToken = default)
        => _innerStore.UpsertFieldAsync(field, cancellationToken);

    public Task UpsertReferenceAsync(Reference reference, CancellationToken cancellationToken = default)
        => _innerStore.UpsertReferenceAsync(reference, cancellationToken);

    public Task<IReadOnlyList<FileSearchResult>> SearchFilesAsync(string query, CancellationToken cancellationToken = default)
        => _innerStore.SearchFilesAsync(query, cancellationToken);

    public Task<IReadOnlyList<ContentSearchResult>> SearchFileContentsAsync(string query, int limit = 100, CancellationToken cancellationToken = default)
        => _innerStore.SearchFileContentsAsync(query, limit, cancellationToken);

    public async Task<ContentSearchOutcome> SearchFileContentsWithCoverageAsync(string query, int limit = 100, CancellationToken cancellationToken = default)
    {
        if (_innerStore is IContentSearchCoverageStore coverageStore)
        {
            return await coverageStore.SearchFileContentsWithCoverageAsync(query, limit, cancellationToken);
        }

        var matches = await _innerStore.SearchFileContentsAsync(query, limit, cancellationToken);
        return new ContentSearchOutcome(matches, new ContentSearchCoverage(matches.Count, limit, null, null, false, false, "unknown", ["The configured store does not report search coverage."]));
    }

    public Task<FileSnapshot?> GetFileSnapshotAsync(string fileIdOrPath, CancellationToken cancellationToken = default)
        => _innerStore.GetFileSnapshotAsync(fileIdOrPath, cancellationToken);

    public async Task<FileSnapshot?> GetFileSnapshotAsync(RepositoryId repositoryId, string repositoryRoot, string relativePath, CancellationToken cancellationToken = default)
    {
        if (_innerStore is not IRootScopedFileSnapshotStore scopedStore) return null;
        var indexedRoot = await _innerStore.GetRepositoryRootPathAsync(repositoryId, cancellationToken);
        return indexedRoot is null
            ? null
            : await scopedStore.GetFileSnapshotAsync(repositoryId, indexedRoot, relativePath, cancellationToken);
    }

    public Task<IReadOnlyList<MethodSummary>> FindMethodSummariesAsync(string query, CancellationToken cancellationToken = default)
        => _innerStore.FindMethodSummariesAsync(query, cancellationToken);

    public Task<IReadOnlyList<MethodSource>> FindMethodSourcesAsync(string query, CancellationToken cancellationToken = default)
        => _innerStore.FindMethodSourcesAsync(query, cancellationToken);

    public Task<IReadOnlyList<TypeSummary>> FindTypeSummariesAsync(string query, CancellationToken cancellationToken = default)
        => _innerStore.FindTypeSummariesAsync(query, cancellationToken);

    public Task<IReadOnlyList<CallerSummary>> FindCallersAsync(string query, CancellationToken cancellationToken = default)
        => _innerStore.FindCallersAsync(query, cancellationToken);

    public Task<IReadOnlyList<CalleeSummary>> FindCalleesAsync(string query, CancellationToken cancellationToken = default)
        => _innerStore.FindCalleesAsync(query, cancellationToken);

    public Task<IReadOnlyList<ReferenceSummary>> FindReferencesAsync(string query, CancellationToken cancellationToken = default)
        => _innerStore.FindReferencesAsync(query, cancellationToken);

    public Task<TypeSummary?> FindTypeSummaryByIdAsync(string typeId, CancellationToken cancellationToken = default)
        => _innerStore.FindTypeSummaryByIdAsync(typeId, cancellationToken);

    public Task<int> CountIndexedFilesAsync(RepositoryId repositoryId, CancellationToken cancellationToken = default)
        => _innerStore.CountIndexedFilesAsync(repositoryId, cancellationToken);

    /// <summary>
    /// Returns the sandbox root instead of the real repository root.
    /// </summary>
    public Task<string?> GetRepositoryRootPathAsync(RepositoryId repositoryId, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(_sandboxRoot);

    public Task DeleteFileAsync(FileId fileId, CancellationToken cancellationToken = default)
        => _innerStore.DeleteFileAsync(fileId, cancellationToken);

    // ISymbolStore delegation
    public Task UpsertSymbolAsync(Symbol symbol, CancellationToken cancellationToken = default)
        => ((ISymbolStore)_innerStore).UpsertSymbolAsync(symbol, cancellationToken);

    public Task<IReadOnlyList<Symbol>> FindSymbolsByNameAsync(string name, CancellationToken cancellationToken = default)
        => ((ISymbolStore)_innerStore).FindSymbolsByNameAsync(name, cancellationToken);

    public Task<IReadOnlyList<Symbol>> FindSymbolsByQualifiedNameAsync(string qualifiedName, CancellationToken cancellationToken = default)
        => ((ISymbolStore)_innerStore).FindSymbolsByQualifiedNameAsync(qualifiedName, cancellationToken);

    public Task<IReadOnlyList<Symbol>> FindSymbolsContainingAsync(string query, CancellationToken cancellationToken = default)
        => ((ISymbolStore)_innerStore).FindSymbolsContainingAsync(query, cancellationToken);

    public Task<Symbol?> FindReadContextTargetAsync(RepositoryId repositoryId, string filePath, string qualifiedName, CancellationToken cancellationToken = default)
        => _innerStore is IReadContextStore contextStore
            ? contextStore.FindReadContextTargetAsync(repositoryId, filePath, qualifiedName, cancellationToken)
            : Task.FromResult<Symbol?>(null);

    public Task<ReadContextCandidates> FindReadContextCandidatesAsync(Symbol target, IReadOnlyList<string> sourceNames, bool sourceNamesTruncated, int limit = 5, CancellationToken cancellationToken = default)
        => _innerStore is IReadContextStore contextStore
            ? contextStore.FindReadContextCandidatesAsync(target, sourceNames, sourceNamesTruncated, limit, cancellationToken)
            : Task.FromResult(new ReadContextCandidates([], false, [], false, false, sourceNamesTruncated || sourceNames.Count > 64));

    public Task<IReadOnlyList<Symbol>> FindClosestSymbolsAsync(string query, int limit = 5, CancellationToken cancellationToken = default)
        => ((ISymbolStore)_innerStore).FindClosestSymbolsAsync(query, limit, cancellationToken);

    // ISessionStore delegation
    public Task<SessionState> CreateSessionAsync(CreateSessionRequest request, CancellationToken cancellationToken = default)
        => ((ISessionStore)_innerStore).CreateSessionAsync(request, cancellationToken);

    public Task<SessionState?> GetSessionAsync(GetSessionRequest request, CancellationToken cancellationToken = default)
        => ((ISessionStore)_innerStore).GetSessionAsync(request, cancellationToken);

    public Task SaveSearchAsync(SaveSearchRequest request, CancellationToken cancellationToken = default)
        => ((ISessionStore)_innerStore).SaveSearchAsync(request, cancellationToken);

    public Task AddWorkingSetItemAsync(AddWorkingSetItemRequest request, CancellationToken cancellationToken = default)
        => ((ISessionStore)_innerStore).AddWorkingSetItemAsync(request, cancellationToken);

    public Task MarkSymbolExpandedAsync(MarkSymbolExpandedRequest request, CancellationToken cancellationToken = default)
        => ((ISessionStore)_innerStore).MarkSymbolExpandedAsync(request, cancellationToken);

    public Task MarkFileExpandedAsync(MarkFileExpandedRequest request, CancellationToken cancellationToken = default)
        => ((ISessionStore)_innerStore).MarkFileExpandedAsync(request, cancellationToken);

    public Task AddDiagnosticAsync(AddDiagnosticRequest request, CancellationToken cancellationToken = default)
        => ((ISessionStore)_innerStore).AddDiagnosticAsync(request, cancellationToken);

    public Task AddExecutionAsync(AddExecutionRequest request, CancellationToken cancellationToken = default)
        => ((ISessionStore)_innerStore).AddExecutionAsync(request, cancellationToken);

    public Task AddOperationMetricAsync(OperationMetric metric, CancellationToken cancellationToken = default)
        => ((ISessionStore)_innerStore).AddOperationMetricAsync(metric, cancellationToken);

    public Task<IReadOnlyList<OperationMetric>> GetOperationMetricsAsync(SessionId sessionId, CancellationToken cancellationToken = default)
        => ((ISessionStore)_innerStore).GetOperationMetricsAsync(sessionId, cancellationToken);

    // IIndexFreshnessStore delegation. A sandbox shares the same persistent fence store, keyed by
    // the sandbox root returned to the protocol engine. Unsupported stores fail closed.
    public Task<IReadOnlyList<DirtyIndexRepository>> GetDirtyIndexRepositoriesAsync(CancellationToken cancellationToken = default)
        => FreshnessStore.GetDirtyIndexRepositoriesAsync(cancellationToken);

    public Task<DirtyIndexRepository?> GetDirtyIndexRepositoryAsync(string repositoryRoot, CancellationToken cancellationToken = default)
        => FreshnessStore.GetDirtyIndexRepositoryAsync(repositoryRoot, cancellationToken);

    public Task<bool> TryMarkIndexFilesDirtyAsync(string repositoryRoot, IReadOnlyList<string> files, CancellationToken cancellationToken = default)
        => FreshnessStore.TryMarkIndexFilesDirtyAsync(repositoryRoot, files, cancellationToken);

    public Task MarkIndexFilesDirtyAsync(string repositoryRoot, IReadOnlyList<string> files, CancellationToken cancellationToken = default)
        => FreshnessStore.MarkIndexFilesDirtyAsync(repositoryRoot, files, cancellationToken);

    public Task MarkIndexRepositoryDirtyAsync(string repositoryRoot, IReadOnlyList<string> files, CancellationToken cancellationToken = default)
        => FreshnessStore.MarkIndexRepositoryDirtyAsync(repositoryRoot, files, cancellationToken);

    public Task ClearDirtyIndexFilesAsync(string repositoryRoot, IReadOnlyList<string> files, CancellationToken cancellationToken = default)
        => FreshnessStore.ClearDirtyIndexFilesAsync(repositoryRoot, files, cancellationToken);

    public Task ClearDirtyIndexRepositoryAsync(string repositoryRoot, CancellationToken cancellationToken = default)
        => FreshnessStore.ClearDirtyIndexRepositoryAsync(repositoryRoot, cancellationToken);

    private IIndexFreshnessStore FreshnessStore
        => _innerStore as IIndexFreshnessStore ?? throw new NotSupportedException("The repository store does not support durable index freshness state.");
}
