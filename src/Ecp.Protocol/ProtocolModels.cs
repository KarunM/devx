using System.Text.Json;
using Ecp.Domain;

namespace Ecp.Protocol;

public sealed record ProtocolRequest(string Version, string Operation, string SessionId, JsonElement Payload);
public sealed record ProtocolResponse(bool Success, JsonElement? Data, IReadOnlyList<ProtocolDiagnostic> Diagnostics, IReadOnlyList<ProtocolError> Errors);
public sealed record ProtocolDiagnostic(string Code, string Message);
public sealed record ProtocolError(string Code, string Message);

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
    public const string RunBuild = "RUN_BUILD";
    public const string RunTests = "RUN_TESTS";
}

public interface IProtocolEngine
{
    Task<ProtocolResponse> ExecuteAsync(ProtocolRequest request, CancellationToken cancellationToken = default);
}

public sealed record PatchRequest(string Version, string SessionId, PatchDocument Patch);
public sealed record DiagnosticsResponse(IReadOnlyList<DiagnosticRecord> Diagnostics);
