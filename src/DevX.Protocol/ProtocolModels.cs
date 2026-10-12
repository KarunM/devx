using System.Text.Json;
using DevX.Application;
using DevX.Domain;

namespace DevX.Protocol;

public sealed record ProtocolRequest(string Version, string Operation, string SessionId, JsonElement Payload, string? AgentLabel = null);
public sealed record ProtocolResponse(bool Success, JsonElement? Data, IReadOnlyList<ProtocolDiagnostic> Diagnostics, IReadOnlyList<ProtocolError> Errors);
public sealed record ProtocolDiagnostic(string Code, string Message);
public sealed record ProtocolError(
    string Code,
    string Message,
    string? AttemptedName = null,
    IReadOnlyList<string>? Suggestions = null,
    IReadOnlyList<SymbolSearchResult>? Candidates = null,
    int? TotalCandidates = null);
public sealed record ValidationTargetRequest(string? Runtime, string? Target);
public sealed record ValidationExecutionResponse(string CapabilityId, RuntimeContext Context, RuntimeTargetDescriptor Target, ExecutionRecord Execution);

/// <summary>`devx verify` in one call: the build, and -- unless the build failed -- the run of the
/// relevant tests. `Tests` is null when the mode was build-only or the build failed, and
/// `TestsSkipped` says which of the two, so the caller never has to infer it.</summary>
public sealed record VerifyExecutionResponse(string Mode, ValidationExecutionResponse Build, ValidationExecutionResponse? Tests, bool TestsSkipped);
public sealed record McpServerInfo(string Name, string Version);
public sealed record McpToolDescriptor(string Name, string Title, string Description, string CapabilityId, JsonElement InputSchema);
public sealed record McpInitializeResponse(string ProtocolVersion, McpServerInfo ServerInfo, IReadOnlyList<McpToolDescriptor> Tools);
public sealed record McpToolCallResult(bool IsError, JsonElement Content);

public static class ProtocolOperations
{
    public const string SearchSymbol = "SEARCH_SYMBOL";
    public const string SearchText = "SEARCH_TEXT";
    public const string GetFileSnapshot = "GET_FILE_SNAPSHOT";
    public const string GetTypeOperation = "GET_TYPE";
    public const string GetMethodSummary = "GET_METHOD_SUMMARY";
    public const string GetMethod = "GET_METHOD";
    public const string GetCallers = "GET_CALLERS";
    public const string GetCallees = "GET_CALLEES";
    public const string GetReferences = "GET_REFERENCES";
    public const string GetTests = "GET_TESTS";
    public const string GetDiagnostics = "GET_DIAGNOSTICS";
    public const string Patch = "PATCH";
    public const string PatchRollback = "PATCH_ROLLBACK";
    public const string RunBuild = "RUN_BUILD";
    public const string RunTests = "RUN_TESTS";
    public const string RunVerify = "RUN_VERIFY";
}

public interface IProtocolEngine
{
    Task<ProtocolResponse> ExecuteAsync(ProtocolRequest request, CancellationToken cancellationToken = default);
}

public sealed record PatchRequest(string Version, string SessionId, PatchDocument Patch);
public sealed record PatchOperationContract(string Operation, string Path, bool RollbackSupported, string RollbackAvailability, string? RollbackReason);
public sealed record PatchLifecycleSummary(
    string LifecycleStage,
    string CapabilityId,
    int Operations,
    int Files,
    IReadOnlyList<string> FilesAffected,
    IReadOnlyList<string> OperationClasses,
    bool RollbackSupported,
    IReadOnlyList<PatchOperationContract> Contracts);
public sealed record PatchApplyResponse(bool Applied, PatchLifecycleSummary Summary, IReadOnlyList<ProtocolDiagnostic> ValidationErrors);
public sealed record PatchPreviewResponse(bool PatchValid, PatchLifecycleSummary Summary, string UnifiedDiffPreview, IReadOnlyList<ProtocolDiagnostic> ValidationErrors);
public sealed record PatchValidateResponse(bool Valid, PatchLifecycleSummary Summary, IReadOnlyList<string> Warnings, IReadOnlyList<string> Errors);
public sealed record PatchRollbackResponse(bool Success, PatchLifecycleSummary Summary, int FilesRestored, int OperationsReverted, IReadOnlyList<string> Warnings, IReadOnlyList<string> Errors);
public sealed record DiagnosticsResponse(IReadOnlyList<DiagnosticRecord> Diagnostics);
public sealed record FileSnapshotResponse(FileSnapshot Snapshot);
public sealed record MethodResponse(MethodSource Method);
public sealed record TypeResponse(TypeSummary Type);
public sealed record CallersResponse(CallerSummaryResponse Callers);
public sealed record CalleesResponse(CalleeSummaryResponse Callees);
public sealed record SourceReadResponse(
    string Kind,
    string SymbolId,
    string QualifiedName,
    string Path,
    int StartLine,
    int EndLine,
    int TotalLines,
    int ReturnedLines,
    bool Truncated,
    bool SourceReturned,
    string Source,
    object Metadata);

public sealed record ReadSourceRequest(string? Query, string? Id, string? Range, bool Full, bool Source);
