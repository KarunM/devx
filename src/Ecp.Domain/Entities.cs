namespace Ecp.Domain;

public readonly record struct RepositoryId(Guid Value);
public readonly record struct ProjectId(Guid Value);
public readonly record struct FileId(Guid Value);
public readonly record struct NamespaceId(Guid Value);
public readonly record struct TypeId(Guid Value);
public readonly record struct MethodId(Guid Value);
public readonly record struct PropertyId(Guid Value);
public readonly record struct FieldId(Guid Value);
public readonly record struct ReferenceId(Guid Value);
public readonly record struct DiagnosticId(Guid Value);
public readonly record struct TestId(Guid Value);
public readonly record struct SessionId(Guid Value);

public sealed record Repository(RepositoryId Id, string Name, string RootPath);
public sealed record Project(ProjectId Id, RepositoryId RepositoryId, string Name, string Path);
public sealed record File(FileId Id, ProjectId ProjectId, string Path);
public sealed record Namespace(NamespaceId Id, FileId FileId, string Name);
public sealed record Type(TypeId Id, NamespaceId NamespaceId, string Name);
public sealed record Method(MethodId Id, TypeId TypeId, string Name);
public sealed record Property(PropertyId Id, TypeId TypeId, string Name);
public sealed record Field(FieldId Id, TypeId TypeId, string Name);
public sealed record Reference(ReferenceId Id, FileId FileId, string TargetSymbolId, string CallerSymbolId, int? CallSiteLine);
public sealed record Diagnostic(DiagnosticId Id, string TargetKind, string Message);
public sealed record Test(TestId Id, FileId FileId, string Name);
public sealed record Session(SessionId Id, RepositoryId RepositoryId, string State);
public sealed record SessionState(
    SessionId Id,
    RepositoryId RepositoryId,
    string? WorkingSet,
    IReadOnlyList<string> SearchHistory,
    IReadOnlyList<string> ExpandedFiles,
    IReadOnlyList<string> ExpandedSymbols,
    IReadOnlyList<string> DiagnosticsHistory,
    IReadOnlyList<string> PatchHistory,
    IReadOnlyList<string> BuildTestHistory);

public sealed record CreateSessionRequest(RepositoryId RepositoryId);
public sealed record GetSessionRequest(SessionId SessionId);
public sealed record SaveSearchRequest(SessionId SessionId, string Query);
public sealed record AddWorkingSetItemRequest(SessionId SessionId, string Item);
public sealed record MarkSymbolExpandedRequest(SessionId SessionId, string SymbolId);
public sealed record MarkFileExpandedRequest(SessionId SessionId, string FileId);
public sealed record AddDiagnosticRequest(SessionId SessionId, DiagnosticRecord Diagnostic);
public sealed record AddExecutionRequest(SessionId SessionId, ExecutionRecord Execution);
public sealed record OperationMetric(
    string Operation,
    DateTimeOffset Timestamp,
    TimeSpan Duration,
    bool Success,
    string? SessionId);
public sealed record SessionMetricsSummary(
    string? SessionId,
    IReadOnlyList<OperationMetric> Operations,
    int IndexedFiles,
    int SearchedFiles,
    int ExpandedFiles,
    int SnapshotsRequested,
    int MethodsExpanded,
    int PatchesApplied,
    int BuildCount,
    int SuccessfulBuilds,
    int FailedBuilds,
    int Warnings,
    int Errors,
    int TestRuns,
    int Passed,
    int Failed,
    int Skipped,
    string LlmMetrics);

public sealed record PatchDocument(string Version, IReadOnlyList<PatchOperation> Operations);
public sealed record PatchOperation(
    string Operation,
    string Path,
    string? Content,
    string? MethodName,
    string? ReplacementText,
    string? ContainingType,
    string? TypeId,
    string? MethodText,
    string? Position,
    string? Namespace);
public sealed record DiagnosticRecord(
    string Severity,
    string Code,
    string Message,
    string? File,
    int? Line,
    int? Column,
    string RawOutput);
public sealed record ExecutionRecord(
    string Command,
    bool Success,
    string RawOutput,
    IReadOnlyList<DiagnosticRecord> Diagnostics);
public sealed record Symbol(
    string Id,
    string RepositoryId,
    string ProjectId,
    string FileId,
    string? ContainerId,
    string Kind,
    string Name,
    string QualifiedName,
    string? Signature);

public sealed record SearchSymbolRequest(string Query);
public sealed record SearchTextRequest(string Query);
public sealed record SearchResult(IReadOnlyList<SymbolSearchResult> Symbols, IReadOnlyList<FileSearchResult> Files);
public sealed record SymbolSearchResult(string Id, string Kind, string Name, string QualifiedName, string? FilePath);
public sealed record FileSearchResult(string Id, string Path, string ProjectName, string RepositoryName);
public sealed record FileSnapshot(
    string Id,
    string Project,
    string Path,
    string Language,
    int ApproximateLoc,
    IReadOnlyList<FileNamespaceSnapshot> Namespaces,
    IReadOnlyList<FileTypeSnapshot> Types,
    IReadOnlyList<FileMethodSnapshot> Methods,
    IReadOnlyList<FilePropertySnapshot> Properties,
    IReadOnlyList<FileFieldSnapshot> Fields,
    IReadOnlyList<string> Dependencies);

public sealed record FileNamespaceSnapshot(string Id, string Name);
public sealed record FileTypeSnapshot(string Id, string Name, string Namespace);
public sealed record FileMethodSnapshot(string Id, string Name, string TypeName, string Namespace);
public sealed record FilePropertySnapshot(string Id, string Name, string TypeName, string Namespace);
public sealed record FileFieldSnapshot(string Id, string Name, string TypeName, string Namespace);
public sealed record MethodSummary(
    string Id,
    string RepositoryName,
    string ProjectName,
    string FilePath,
    string Namespace,
    string TypeName,
    string Name,
    string QualifiedName,
    string Kind);

public sealed record ReadSourceResponse(
    string Kind,
    string SymbolId,
    string QualifiedName,
    string Path,
    int StartLine,
    int EndLine,
    int TotalLines,
    int ReturnedLines,
    bool Truncated,
    string Source,
    bool SourceReturned,
    object? Metadata);

public sealed record MethodSummaryResponse(
    string Query,
    string MatchKind,
    MethodSummary? SelectedMethod,
    IReadOnlyList<MethodSummary> Methods);

public sealed record MethodSource(
    string Id,
    string QualifiedName,
    string FilePath,
    int StartLine,
    int EndLine,
    string Language,
    string Source);

public sealed record TypeSummary(
    string Id,
    string Name,
    string QualifiedName,
    string Kind,
    string Namespace,
    string Project,
    string FilePath,
    string? BaseType,
    IReadOnlyList<string> ImplementedInterfaces,
    IReadOnlyList<string> GenericParameters,
    string Visibility,
    IReadOnlyList<FileMethodSnapshot> Constructors,
    IReadOnlyList<FilePropertySnapshot> Properties,
    IReadOnlyList<FileFieldSnapshot> Fields,
    IReadOnlyList<FileMethodSnapshot> Methods,
    IReadOnlyList<string> NestedTypes,
    int ApproximateLoc,
    int StartLine,
    int EndLine);

public sealed record TypeSummaryResponse(
    string Query,
    string MatchKind,
    TypeSummary? SelectedType,
    IReadOnlyList<TypeSummary> Types);

public sealed record CallerSummary(
    string CallerMethodId,
    string CallerMethodName,
    string QualifiedName,
    string Namespace,
    string Project,
    string FilePath,
    string ContainingType,
    int? CallSiteLine);

public sealed record CallerSummaryResponse(
    string Query,
    string MatchKind,
    MethodSummary? TargetMethod,
    IReadOnlyList<CallerSummary> Callers);

public sealed record CalleeSummary(
    string CalleeMethodId,
    string CalleeMethodName,
    string QualifiedName,
    string Namespace,
    string Project,
    string FilePath,
    string ContainingType,
    int? CallSiteLine);

public sealed record CalleeSummaryResponse(
    string Query,
    string MatchKind,
    MethodSummary? TargetMethod,
    IReadOnlyList<CalleeSummary> Callees);

public sealed record ReferenceSummary(
    string ReferenceId,
    string SymbolName,
    string QualifiedName,
    string SymbolKind,
    string ReferencingMethod,
    string ReferencingType,
    string Namespace,
    string Project,
    string FilePath,
    int? LineNumber);

public sealed record ReferencesResponse(
    string Query,
    string MatchKind,
    MethodSummary? TargetSymbol,
    IReadOnlyList<ReferenceSummary> References);

public sealed record IndexStatusResponse(
    bool RepositoryIndexed,
    string RepositoryRoot,
    int IndexedFiles,
    bool IndexExists,
    string Status,
    string Reason);

public interface ISearchService
{
    Task<SearchResult> SearchSymbolAsync(SearchSymbolRequest request, CancellationToken cancellationToken = default);
    Task<SearchResult> SearchTextAsync(SearchTextRequest request, CancellationToken cancellationToken = default);
}

public sealed record ParsedFile(
    string FilePath,
    string? Namespace,
    IReadOnlyList<ParsedType> Types);

public sealed record ParsedType(
    string Name,
    string Kind,
    string? Namespace,
    string Visibility,
    int StartLine,
    int EndLine,
    IReadOnlyList<ParsedMethod> Methods,
    IReadOnlyList<ParsedProperty> Properties,
    IReadOnlyList<ParsedField> Fields);

public sealed record ParsedMethod(
    string Name,
    string Signature,
    string ReturnType,
    string Visibility,
    int StartLine,
    int EndLine,
    bool IsConstructor);

public sealed record ParsedProperty(
    string Name,
    string Type,
    string Visibility,
    int StartLine,
    int EndLine);

public sealed record ParsedField(
    string Name,
    string Type,
    string Visibility,
    int StartLine,
    int EndLine);

public interface ILanguageParser
{
    bool CanParse(string filePath);
    ParsedFile Parse(string filePath, string sourceText);
}

public interface IRepositoryStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task UpsertRepositoryAsync(Repository repository, CancellationToken cancellationToken = default);
    Task UpsertProjectAsync(Project project, CancellationToken cancellationToken = default);
    Task UpsertFileAsync(File file, CancellationToken cancellationToken = default);
    Task UpsertNamespaceAsync(Namespace @namespace, CancellationToken cancellationToken = default);
    Task UpsertTypeAsync(Type type, CancellationToken cancellationToken = default);
    Task UpsertMethodAsync(Method method, CancellationToken cancellationToken = default);
    Task UpsertPropertyAsync(Property property, CancellationToken cancellationToken = default);
    Task UpsertFieldAsync(Field field, CancellationToken cancellationToken = default);
    Task UpsertReferenceAsync(Reference reference, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FileSearchResult>> SearchFilesAsync(string query, CancellationToken cancellationToken = default);
    Task<FileSnapshot?> GetFileSnapshotAsync(string fileIdOrPath, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MethodSummary>> FindMethodSummariesAsync(string query, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MethodSource>> FindMethodSourcesAsync(string query, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TypeSummary>> FindTypeSummariesAsync(string query, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CallerSummary>> FindCallersAsync(string query, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CalleeSummary>> FindCalleesAsync(string query, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReferenceSummary>> FindReferencesAsync(string query, CancellationToken cancellationToken = default);
    Task<TypeSummary?> FindTypeSummaryByIdAsync(string typeId, CancellationToken cancellationToken = default);
    Task<int> CountIndexedFilesAsync(RepositoryId repositoryId, CancellationToken cancellationToken = default);
    Task<string?> GetRepositoryRootPathAsync(RepositoryId repositoryId, CancellationToken cancellationToken = default);
}

public interface ISymbolStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task UpsertSymbolAsync(Symbol symbol, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Symbol>> FindSymbolsByNameAsync(string name, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Symbol>> FindSymbolsByQualifiedNameAsync(string qualifiedName, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Symbol>> FindSymbolsContainingAsync(string query, CancellationToken cancellationToken = default);
}

public interface ISessionStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<SessionState> CreateSessionAsync(CreateSessionRequest request, CancellationToken cancellationToken = default);
    Task<SessionState?> GetSessionAsync(GetSessionRequest request, CancellationToken cancellationToken = default);
    Task SaveSearchAsync(SaveSearchRequest request, CancellationToken cancellationToken = default);
    Task AddWorkingSetItemAsync(AddWorkingSetItemRequest request, CancellationToken cancellationToken = default);
    Task MarkSymbolExpandedAsync(MarkSymbolExpandedRequest request, CancellationToken cancellationToken = default);
    Task MarkFileExpandedAsync(MarkFileExpandedRequest request, CancellationToken cancellationToken = default);
    Task AddDiagnosticAsync(AddDiagnosticRequest request, CancellationToken cancellationToken = default);
    Task AddExecutionAsync(AddExecutionRequest request, CancellationToken cancellationToken = default);
    Task AddOperationMetricAsync(OperationMetric metric, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OperationMetric>> GetOperationMetricsAsync(SessionId sessionId, CancellationToken cancellationToken = default);
}
