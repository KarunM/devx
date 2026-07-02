using System.Text.Json;
using Ecp.Domain;
using Ecp.Indexer;
using IOFile = System.IO.File;

namespace Ecp.Protocol;

public sealed class ProtocolEngine : IProtocolEngine
{
    private readonly ISearchService _searchService;
    private readonly ISessionStore _sessionStore;
    private readonly IRepositoryStore _repositoryStore;
    private readonly DotnetBuildTestService _buildTestService;

    public ProtocolEngine(ISearchService searchService, ISessionStore sessionStore, IRepositoryStore repositoryStore)
        : this(searchService, sessionStore, repositoryStore, new DotnetBuildTestService())
    {
    }

    public ProtocolEngine(ISearchService searchService, ISessionStore sessionStore, IRepositoryStore repositoryStore, DotnetBuildTestService buildTestService)
    {
        _searchService = searchService;
        _sessionStore = sessionStore;
        _repositoryStore = repositoryStore;
        _buildTestService = buildTestService;
    }

    public async Task<ProtocolResponse> ExecuteAsync(ProtocolRequest request, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(request.Version, "1.0", StringComparison.Ordinal))
        {
            return Fail("UnsupportedVersion", $"Unsupported protocol version '{request.Version}'.");
        }

        var session = await _sessionStore.GetSessionAsync(new GetSessionRequest(ParseSessionId(request.SessionId)), cancellationToken);
        if (session is null)
        {
            return Fail("SessionNotFound", $"Session '{request.SessionId}' was not found.");
        }

        return request.Operation switch
        {
            ProtocolOperations.SearchSymbol => await HandleSearchSymbolAsync(request, session, cancellationToken),
            ProtocolOperations.SearchText => await HandleSearchTextAsync(request, session, cancellationToken),
            ProtocolOperations.Patch => await HandlePatchAsync(request, session, cancellationToken),
            ProtocolOperations.RunBuild => await HandleRunBuildAsync(session, cancellationToken),
            ProtocolOperations.RunTests => await HandleRunTestsAsync(session, cancellationToken),
            ProtocolOperations.GetDiagnostics => await HandleGetDiagnosticsAsync(session, cancellationToken),
            ProtocolOperations.GetFileSnapshot or ProtocolOperations.GetTypeOperation or ProtocolOperations.GetMethodSummary or ProtocolOperations.GetMethod or ProtocolOperations.GetCallers or ProtocolOperations.GetCallees or ProtocolOperations.GetReferences or ProtocolOperations.GetTests
                => Fail("HandlerNotImplemented", $"Operation '{request.Operation}' is not implemented yet."),
            _ => Fail("InvalidOperation", $"Unknown operation '{request.Operation}'.")
        };
    }

    private async Task<ProtocolResponse> HandleSearchSymbolAsync(ProtocolRequest request, SessionState session, CancellationToken cancellationToken)
    {
        if (!TryGetQuery(request.Payload, out var query))
        {
            return Fail("ValidationError", "Search requests must include a string 'query' field.");
        }

        var result = await _searchService.SearchSymbolAsync(new SearchSymbolRequest(query), cancellationToken);
        await _sessionStore.SaveSearchAsync(new SaveSearchRequest(session.Id, query), cancellationToken);
        return Success(result);
    }

    private async Task<ProtocolResponse> HandleSearchTextAsync(ProtocolRequest request, SessionState session, CancellationToken cancellationToken)
    {
        if (!TryGetQuery(request.Payload, out var query))
        {
            return Fail("ValidationError", "Search requests must include a string 'query' field.");
        }

        var result = await _searchService.SearchTextAsync(new SearchTextRequest(query), cancellationToken);
        await _sessionStore.SaveSearchAsync(new SaveSearchRequest(session.Id, query), cancellationToken);
        return Success(result);
    }

    private static ProtocolResponse Success(SearchResult result)
        => new(true, JsonSerializer.SerializeToElement(result), [], []);

    private async Task<ProtocolResponse> HandleRunBuildAsync(SessionState session, CancellationToken cancellationToken)
    {
        var root = await _repositoryStore.GetRepositoryRootPathAsync(session.RepositoryId, cancellationToken);
        if (string.IsNullOrWhiteSpace(root))
        {
            return Fail("RepositoryNotIndexed", "Repository root was not found.");
        }

        var execution = await _buildTestService.RunBuildAsync(root, cancellationToken);
        await _sessionStore.AddExecutionAsync(new AddExecutionRequest(session.Id, execution), cancellationToken);
        foreach (var diagnostic in execution.Diagnostics)
        {
            await _sessionStore.AddDiagnosticAsync(new AddDiagnosticRequest(session.Id, diagnostic), cancellationToken);
        }

        return new ProtocolResponse(execution.Success, JsonSerializer.SerializeToElement(execution), [], execution.Success ? [] : [new ProtocolError("ValidationError", "Build failed.")]);
    }

    private async Task<ProtocolResponse> HandleRunTestsAsync(SessionState session, CancellationToken cancellationToken)
    {
        var root = await _repositoryStore.GetRepositoryRootPathAsync(session.RepositoryId, cancellationToken);
        if (string.IsNullOrWhiteSpace(root))
        {
            return Fail("RepositoryNotIndexed", "Repository root was not found.");
        }

        var execution = await _buildTestService.RunTestsAsync(root, cancellationToken);
        await _sessionStore.AddExecutionAsync(new AddExecutionRequest(session.Id, execution), cancellationToken);
        foreach (var diagnostic in execution.Diagnostics)
        {
            await _sessionStore.AddDiagnosticAsync(new AddDiagnosticRequest(session.Id, diagnostic), cancellationToken);
        }

        return new ProtocolResponse(execution.Success, JsonSerializer.SerializeToElement(execution), [], execution.Success ? [] : [new ProtocolError("ValidationError", "Tests failed.")]);
    }

    private async Task<ProtocolResponse> HandleGetDiagnosticsAsync(SessionState session, CancellationToken cancellationToken)
    {
        var current = await _sessionStore.GetSessionAsync(new GetSessionRequest(session.Id), cancellationToken);
        var diagnostics = new List<DiagnosticRecord>();
        if (current is not null)
        {
            foreach (var entry in current.DiagnosticsHistory)
            {
                var diagnostic = JsonSerializer.Deserialize<DiagnosticRecord>(entry);
                if (diagnostic is not null)
                {
                    diagnostics.Add(diagnostic);
                }
            }
        }
        return new ProtocolResponse(true, JsonSerializer.SerializeToElement(new DiagnosticsResponse(diagnostics)), [], []);
    }

    private async Task<ProtocolResponse> HandlePatchAsync(ProtocolRequest request, SessionState session, CancellationToken cancellationToken)
    {
        var patch = JsonSerializer.Deserialize<PatchDocument>(request.Payload.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (patch is null || patch.Version != "1.0")
        {
            return Fail("ValidationError", "Invalid patch document.");
        }

        var root = await _repositoryStore.GetRepositoryRootPathAsync(session.RepositoryId, cancellationToken);
        if (string.IsNullOrWhiteSpace(root))
        {
            return Fail("RepositoryNotIndexed", "Repository root was not found.");
        }

        var results = new List<ProtocolDiagnostic>();
        foreach (var operation in patch.Operations)
        {
            var result = ApplyOperation(root, operation);
            if (!result.Success)
            {
                return Fail(result.Code, result.Message);
            }
            results.Add(new ProtocolDiagnostic("PatchApplied", operation.Operation));
        }

        return new ProtocolResponse(true, JsonSerializer.SerializeToElement(new { applied = patch.Operations.Count }), results, []);
    }

    private static (bool Success, string Code, string Message) ApplyOperation(string root, PatchOperation operation)
    {
        var normalizedRoot = Path.GetFullPath(root);
        var targetPath = Path.GetFullPath(Path.Combine(normalizedRoot, operation.Path));
        if (!targetPath.StartsWith(normalizedRoot, StringComparison.Ordinal))
        {
            return (false, "ValidationError", "Patch targets must remain inside the repository root.");
        }

        return operation.Operation switch
        {
            "CreateFile" => ApplyCreateFile(targetPath, operation.Content),
            "DeleteFile" => ApplyDeleteFile(targetPath),
            "ReplaceMethod" => ApplyReplaceMethod(targetPath, operation.MethodName, operation.ReplacementText),
            _ => (false, "InvalidOperation", $"Unsupported patch operation '{operation.Operation}'.")
        };
    }

    private static (bool Success, string Code, string Message) ApplyCreateFile(string targetPath, string? content)
    {
        if (IOFile.Exists(targetPath))
        {
            return (false, "ValidationError", "Target file already exists.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        IOFile.WriteAllText(targetPath, content ?? string.Empty);
        return (true, string.Empty, string.Empty);
    }

    private static (bool Success, string Code, string Message) ApplyDeleteFile(string targetPath)
    {
        if (!IOFile.Exists(targetPath))
        {
            return (false, "NotFound", "Target file does not exist.");
        }

        IOFile.Delete(targetPath);
        return (true, string.Empty, string.Empty);
    }

    private static (bool Success, string Code, string Message) ApplyReplaceMethod(string targetPath, string? methodName, string? replacementText)
    {
        if (!IOFile.Exists(targetPath))
        {
            return (false, "NotFound", "Target file does not exist.");
        }

        if (string.IsNullOrWhiteSpace(methodName) || string.IsNullOrWhiteSpace(replacementText))
        {
            return (false, "ValidationError", "ReplaceMethod requires methodName and replacementText.");
        }

        var text = IOFile.ReadAllText(targetPath);
        var matches = System.Text.RegularExpressions.Regex.Matches(text, $@"[^\n]*\b{System.Text.RegularExpressions.Regex.Escape(methodName)}\b[^\n]*\{{[\s\S]*?\}}");
        if (matches.Count != 1)
        {
            return (false, "ValidationError", "Target method must be unambiguous.");
        }

        var match = matches[0];
        var updated = text[..match.Index] + replacementText + text[(match.Index + match.Length)..];
        IOFile.WriteAllText(targetPath, updated);
        return (true, string.Empty, string.Empty);
    }

    private static ProtocolResponse Fail(string code, string message)
        => new(false, null, [], [new ProtocolError(code, message)]);

    private static SessionId ParseSessionId(string sessionId) => new(Guid.Parse(sessionId));

    private static bool TryGetQuery(JsonElement payload, out string query)
    {
        query = string.Empty;
        return payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("query", out var queryElement)
            && queryElement.ValueKind == JsonValueKind.String
            && (query = queryElement.GetString() ?? string.Empty).Length > 0;
    }
}
