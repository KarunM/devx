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
public sealed record Reference(ReferenceId Id, FileId FileId, string TargetSymbolId);
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

public sealed record PatchDocument(string Version, IReadOnlyList<PatchOperation> Operations);
public sealed record PatchOperation(string Operation, string Path, string? Content, string? MethodName, string? ReplacementText);
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
    Task<IReadOnlyList<FileSearchResult>> SearchFilesAsync(string query, CancellationToken cancellationToken = default);
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
}
