using System.Text.Json.Serialization;

namespace DevX.Domain;

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
    string? SessionId,
    string? MetadataJson = null,
    string? AgentLabel = null);
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
    string? Namespace,
    string? BeforeState,
    string? OldText = null,
    int? Occurrence = null,
    bool ReplaceAll = false);
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
    IReadOnlyList<DiagnosticRecord> Diagnostics,
    string Summary = "");
public sealed record Symbol(
    string Id,
    string RepositoryId,
    string ProjectId,
    string FileId,
    string? ContainerId,
    string Kind,
    string Name,
    string QualifiedName,
    string? Signature,
    int? StartLine = null,
    int? EndLine = null,
    string? FilePath = null);

public sealed record ReadContextCandidates(
    IReadOnlyList<Symbol> TestNameMatches,
    bool TestNameMatchesTruncated,
    IReadOnlyList<Symbol> SameFileDefinitions,
    bool SameFileDefinitionsTruncated,
    bool SameFileDefinitionsAreNameMatches,
    bool SourceNamesTruncated);

public sealed record SearchSymbolRequest(string Query);
public sealed record SearchTextRequest(string Query);
public sealed record SearchResult(
    IReadOnlyList<SymbolSearchResult> Symbols,
    IReadOnlyList<FileSearchResult> Files,
    IReadOnlyList<ContentSearchResult>? ContentMatches = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ContentSearchCoverage? ContentCoverage = null);
public sealed record SymbolSearchResult(string Id, string Kind, string Name, string QualifiedName, string? FilePath, int? StartLine = null, int? EndLine = null, string? FileStatus = null);
public sealed record FileSearchResult(string Id, string Path, string ProjectName, string RepositoryName, string? FileStatus = null);
public sealed record ContentSearchResult(string FileId, string Path, int Line, int Column, string Preview);

/// <summary>How much of the indexed global content search was actually observed.</summary>
/// <param name="OutputTruncated">True means a result beyond the output limit was observed; false means complete coverage established that no result was omitted; null means truncation is unknown.</param>
/// <param name="Complete">True means every indexed file was scanned; false means an extra hit proved output was omitted; null means coverage is unknown.</param>
public sealed record ContentSearchCoverage(
    int ReturnedCount,
    int ResultLimit,
    bool? OutputTruncated,
    bool? Complete,
    bool ScanCapped,
    bool FallbackUsed,
    string Engine,
    IReadOnlyList<string> Warnings,
    int UnindexedHits = 0);

public sealed record ContentSearchOutcome(
    IReadOnlyList<ContentSearchResult> Matches,
    ContentSearchCoverage Coverage);
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
public sealed record FileTypeSnapshot(string Id, string Name, string Namespace,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? StartLine = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? EndLine = null);
public sealed record FileMethodSnapshot(string Id, string Name, string TypeName, string Namespace,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Signature = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? StartLine = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? EndLine = null);
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
    int? CallSiteLine,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Relationship = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CallerSignature = null);

/// <summary>One overload of a method and the callers attributed to it (`Attribution` says how: "arity" = argument count against the parameter list).</summary>
public sealed record CallerOverload(string Signature, string Path, int StartLine, string Attribution, IReadOnlyList<CallerSummary> Callers);

public sealed record CallerSummaryResponse(
    string Query,
    string MatchKind,
    MethodSummary? TargetMethod,
    IReadOnlyList<CallerSummary> Callers,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TargetSignature = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<CallerOverload>? Overloads = null);

/// <summary>A method row with what a call-graph scan needs: the symbol (signature and span included) and where it lives.</summary>
public sealed record MethodCandidate(Symbol Symbol, string Namespace, string Project, string TypeName, string RootPath);

public sealed record CalleeSummary(
    string CalleeMethodId,
    string CalleeMethodName,
    string QualifiedName,
    string Namespace,
    string Project,
    string FilePath,
    string ContainingType,
    int? CallSiteLine,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Relationship = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CalleeSignature = null);

/// <summary>One overload of a method and the methods its own body calls.</summary>
public sealed record CalleeOverload(string Signature, string Path, int StartLine, IReadOnlyList<CalleeSummary> Callees);

public sealed record CalleeSummaryResponse(
    string Query,
    string MatchKind,
    MethodSummary? TargetMethod,
    IReadOnlyList<CalleeSummary> Callees,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TargetSignature = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<CalleeOverload>? Overloads = null);

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

/// <summary>One text-backed usage of a name: where it is, which indexed symbol encloses the line, and what kind of use it is.</summary>
public sealed record TextReference(string Path, int Line, string Enclosing, string Text, string Kind);

public sealed record TextReferenceMetadata(string? Note);

/// <summary>Call sites of one enclosing symbol, for the grouped `callers` view.</summary>
public sealed record TextReferenceGroup(string Enclosing, int Count, string Path, IReadOnlyList<int> Lines);

/// <summary>`references` / `callers` for languages without a resolved call graph (Python): a whole-word
/// usage search over indexed files, attributed to the enclosing indexed symbol. `Mode` is "text"; the
/// symbol-table (C#) responses are unchanged and carry no Mode.</summary>
public sealed record TextReferencesResponse(
    string Mode,
    string Query,
    string Target,
    string TargetPath,
    int TargetLine,
    int Total,
    int Returned,
    bool Truncated,
    int Calls,
    int Usages,
    int Imports,
    ContentSearchCoverage? Coverage,
    IReadOnlyList<TextReference> References,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TextReferenceMetadata? Metadata = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Grouped = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<TextReferenceGroup>? Groups = null);

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
    Task<IReadOnlyList<ContentSearchResult>> SearchFileContentsAsync(string query, int limit = 100, CancellationToken cancellationToken = default);
    Task<FileSnapshot?> GetFileSnapshotAsync(string fileIdOrPath, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MethodSummary>> FindMethodSummariesAsync(string query, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MethodSource>> FindMethodSourcesAsync(string query, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TypeSummary>> FindTypeSummariesAsync(string query, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CallerSummary>> FindCallersAsync(string query, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CalleeSummary>> FindCalleesAsync(string query, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReferenceSummary>> FindReferencesAsync(string query, CancellationToken cancellationToken = default);
    Task<TypeSummary?> FindTypeSummaryByIdAsync(string typeId, CancellationToken cancellationToken = default);
    Task<int> CountIndexedFilesAsync(RepositoryId repositoryId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetIndexedFilePathsAsync(string root, CancellationToken cancellationToken = default);
    Task<string?> GetRepositoryRootPathAsync(RepositoryId repositoryId, CancellationToken cancellationToken = default);
    Task DeleteFileAsync(FileId fileId, CancellationToken cancellationToken = default);
}

/// <summary>Optional richer content-search contract; legacy stores may continue implementing IRepositoryStore only.</summary>
public interface IContentSearchCoverageStore
{
    Task<ContentSearchOutcome> SearchFileContentsWithCoverageAsync(string query, int limit = 100, CancellationToken cancellationToken = default);
}

/// <summary>Provides an indexed file snapshot scoped to a registered repository root.</summary>
public interface IRootScopedFileSnapshotStore
{
    Task<FileSnapshot?> GetFileSnapshotAsync(RepositoryId repositoryId, string repositoryRoot, string relativePath, CancellationToken cancellationToken = default);
}

public interface ISymbolStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task UpsertSymbolAsync(Symbol symbol, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Symbol>> FindSymbolsByNameAsync(string name, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Symbol>> FindSymbolsByQualifiedNameAsync(string qualifiedName, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Symbol>> FindSymbolsContainingAsync(string query, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Symbol>> FindClosestSymbolsAsync(string query, int limit = 5, CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional bounded navigation metadata for a resolved symbol read.
/// </summary>
public interface IReadContextStore
{
    Task<Symbol?> FindReadContextTargetAsync(RepositoryId repositoryId, string filePath, string qualifiedName, CancellationToken cancellationToken = default);
    Task<ReadContextCandidates> FindReadContextCandidatesAsync(Symbol target, IReadOnlyList<string> sourceNames, bool sourceNamesTruncated, int limit = 5, CancellationToken cancellationToken = default);
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

/// <summary>
/// Durable index state for source paths that may have changed without a confirmed index refresh.
/// A full-reindex fence cannot be cleared by refreshing only an arbitrary subset of files.
/// </summary>
public sealed record DirtyIndexRepository(string RepositoryRoot, bool RequiresFullReindex, IReadOnlyList<string> DirtyFiles);

public interface IIndexFreshnessStore
{
    Task<IReadOnlyList<DirtyIndexRepository>> GetDirtyIndexRepositoriesAsync(CancellationToken cancellationToken = default);
    Task<DirtyIndexRepository?> GetDirtyIndexRepositoryAsync(string repositoryRoot, CancellationToken cancellationToken = default);
    Task<bool> TryMarkIndexFilesDirtyAsync(string repositoryRoot, IReadOnlyList<string> files, CancellationToken cancellationToken = default);
    Task MarkIndexFilesDirtyAsync(string repositoryRoot, IReadOnlyList<string> files, CancellationToken cancellationToken = default);
    Task MarkIndexRepositoryDirtyAsync(string repositoryRoot, IReadOnlyList<string> files, CancellationToken cancellationToken = default);
    Task ClearDirtyIndexFilesAsync(string repositoryRoot, IReadOnlyList<string> files, CancellationToken cancellationToken = default);
    Task ClearDirtyIndexRepositoryAsync(string repositoryRoot, CancellationToken cancellationToken = default);
}
