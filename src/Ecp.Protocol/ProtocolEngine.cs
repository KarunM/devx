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
            ProtocolOperations.GetFileSnapshot => await HandleGetFileSnapshotAsync(request, session, cancellationToken),
            ProtocolOperations.GetMethodSummary => await HandleGetMethodSummaryAsync(request, session, cancellationToken),
            ProtocolOperations.GetMethod => await HandleGetMethodAsync(request, session, cancellationToken),
            ProtocolOperations.GetTypeOperation => await HandleGetTypeAsync(request, session, cancellationToken),
            ProtocolOperations.GetCallers => await HandleGetCallersAsync(request, session, cancellationToken),
            ProtocolOperations.GetCallees => await HandleGetCalleesAsync(request, session, cancellationToken),
            ProtocolOperations.GetReferences => await HandleGetReferencesAsync(request, session, cancellationToken),
            ProtocolOperations.Patch => await HandlePatchAsync(request, session, cancellationToken),
            "PATCH_PREVIEW" => await HandlePatchPreviewAsync(request, session, cancellationToken),
            "PATCH_VALIDATE" => await HandlePatchValidateAsync(request, session, cancellationToken),
            ProtocolOperations.PatchRollback => await HandlePatchRollbackAsync(request, session, cancellationToken),
            ProtocolOperations.RunBuild => await HandleRunBuildAsync(session, cancellationToken),
            ProtocolOperations.RunTests => await HandleRunTestsAsync(session, cancellationToken),
            ProtocolOperations.GetDiagnostics => await HandleGetDiagnosticsAsync(session, cancellationToken),
            ProtocolOperations.GetMethod or ProtocolOperations.GetCallers or ProtocolOperations.GetCallees or ProtocolOperations.GetTests
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

    private async Task<ProtocolResponse> HandleGetFileSnapshotAsync(ProtocolRequest request, SessionState session, CancellationToken cancellationToken)
    {
        if (!TryGetQuery(request.Payload, out var fileIdOrPath))
        {
            return Fail("ValidationError", "Snapshot requests must include a string 'query' field.");
        }

        var snapshot = await _repositoryStore.GetFileSnapshotAsync(fileIdOrPath, cancellationToken);
        if (snapshot is null)
        {
            return Fail("NotFound", $"File '{fileIdOrPath}' was not found.");
        }

        await _sessionStore.AddWorkingSetItemAsync(new AddWorkingSetItemRequest(session.Id, fileIdOrPath), cancellationToken);
        return new ProtocolResponse(true, JsonSerializer.SerializeToElement(new FileSnapshotResponse(snapshot)), [], []);
    }

    private async Task<ProtocolResponse> HandleGetMethodSummaryAsync(ProtocolRequest request, SessionState session, CancellationToken cancellationToken)
    {
        if (!TryGetQuery(request.Payload, out var query))
        {
            return Fail("ValidationError", "Method summary requests must include a string 'query' field.");
        }

        var methods = await _repositoryStore.FindMethodSummariesAsync(query, cancellationToken);
        if (methods.Count == 0)
        {
            return Fail("NotFound", $"Method '{query}' was not found.");
        }

        var exactMatch = methods.FirstOrDefault(method =>
            string.Equals(method.Name, query, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(method.QualifiedName, query, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(method.Id, query, StringComparison.OrdinalIgnoreCase));

        var matchKind = methods.Count == 1
            ? "Exact"
            : exactMatch is not null
                ? "Ambiguous"
                : "Partial";

        var selected = exactMatch is not null && methods.Count == 1 ? exactMatch : null;
        var workingSetItem = selected?.Id ?? methods[0].Id;
        await _sessionStore.AddWorkingSetItemAsync(new AddWorkingSetItemRequest(session.Id, workingSetItem), cancellationToken);
        if (selected is not null)
        {
            await _sessionStore.MarkSymbolExpandedAsync(new MarkSymbolExpandedRequest(session.Id, selected.Id), cancellationToken);
        }

        return new ProtocolResponse(true, JsonSerializer.SerializeToElement(new MethodSummaryResponse(query, matchKind, selected, methods)), [], []);
    }

    private async Task<ProtocolResponse> HandleGetMethodAsync(ProtocolRequest request, SessionState session, CancellationToken cancellationToken)
    {
        if (!TryGetQuery(request.Payload, out var query))
        {
            return Fail("ValidationError", "Method requests must include a string 'query' field.");
        }

        var methods = await _repositoryStore.FindMethodSourcesAsync(query, cancellationToken);
        if (methods.Count == 0)
        {
            return Fail("NotFound", $"Method '{query}' was not found.");
        }

        if (methods.Count > 1)
        {
            return Fail("AmbiguousMatch", $"Method '{query}' matched multiple implementations.");
        }

        var method = methods[0];
        await _sessionStore.AddWorkingSetItemAsync(new AddWorkingSetItemRequest(session.Id, method.Id), cancellationToken);
        await _sessionStore.MarkSymbolExpandedAsync(new MarkSymbolExpandedRequest(session.Id, method.Id), cancellationToken);
        return new ProtocolResponse(true, JsonSerializer.SerializeToElement(new MethodResponse(method)), [], []);
    }

    private async Task<ProtocolResponse> HandleGetTypeAsync(ProtocolRequest request, SessionState session, CancellationToken cancellationToken)
    {
        if (!TryGetQuery(request.Payload, out var query))
        {
            return Fail("ValidationError", "Type requests must include a string 'query' field.");
        }

        var types = await _repositoryStore.FindTypeSummariesAsync(query, cancellationToken);
        if (types.Count == 0)
        {
            return Fail("NotFound", $"Type '{query}' was not found.");
        }

        if (types.Count > 1)
        {
            return Fail("AmbiguousMatch", $"Type '{query}' matched multiple implementations.");
        }

        var type = types[0];
        await _sessionStore.AddWorkingSetItemAsync(new AddWorkingSetItemRequest(session.Id, type.Id), cancellationToken);
        await _sessionStore.MarkSymbolExpandedAsync(new MarkSymbolExpandedRequest(session.Id, type.Id), cancellationToken);
        return new ProtocolResponse(true, JsonSerializer.SerializeToElement(new TypeResponse(type)), [], []);
    }

    private async Task<ProtocolResponse> HandleGetCallersAsync(ProtocolRequest request, SessionState session, CancellationToken cancellationToken)
    {
        if (!TryGetQuery(request.Payload, out var query))
        {
            return Fail("ValidationError", "Caller requests must include a string 'query' field.");
        }

        var callers = await _repositoryStore.FindCallersAsync(query, cancellationToken);
        if (callers.Count == 0)
        {
            return Fail("NotFound", $"Method '{query}' was not found.");
        }

        var methods = await _repositoryStore.FindMethodSummariesAsync(query, cancellationToken);
        var target = methods.Count == 1 ? methods[0] : null;
        if (methods.Count > 1)
        {
            return Fail("AmbiguousMatch", $"Method '{query}' matched multiple implementations.");
        }

        foreach (var caller in callers)
        {
            await _sessionStore.AddWorkingSetItemAsync(new AddWorkingSetItemRequest(session.Id, caller.CallerMethodId), cancellationToken);
        }

        return new ProtocolResponse(true, JsonSerializer.SerializeToElement(new CallersResponse(new CallerSummaryResponse(query, target is null ? "Unknown" : "Exact", target, callers))), [], []);
    }

    private async Task<ProtocolResponse> HandleGetReferencesAsync(ProtocolRequest request, SessionState session, CancellationToken cancellationToken)
    {
        if (!TryGetQuery(request.Payload, out var query))
        {
            return Fail("ValidationError", "Reference requests must include a string 'query' field.");
        }

        var references = await _repositoryStore.FindReferencesAsync(query, cancellationToken);
        if (references.Count == 0)
        {
            return Fail("NotFound", $"Symbol '{query}' was not found.");
        }

        var targetSearch = await _repositoryStore.FindMethodSummariesAsync(query, cancellationToken);

        var response = new ReferencesResponse(
            Query: query,
            MatchKind: "Exact",
            TargetSymbol: targetSearch.Count > 0 ? targetSearch[0] : null,
            References: references);

        return new ProtocolResponse(true, JsonSerializer.SerializeToElement(response), [], []);
    }
    private async Task<ProtocolResponse> HandleGetCalleesAsync(ProtocolRequest request, SessionState session, CancellationToken cancellationToken)
    {
        if (!TryGetQuery(request.Payload, out var query))
        {
            return Fail("ValidationError", "Callee requests must include a string 'query' field.");
        }

        var callees = await _repositoryStore.FindCalleesAsync(query, cancellationToken);
        if (callees.Count == 0)
        {
            return Fail("NotFound", $"Method '{query}' was not found.");
        }

        var methods = await _repositoryStore.FindMethodSummariesAsync(query, cancellationToken);
        var target = methods.Count == 1 ? methods[0] : null;
        if (methods.Count > 1)
        {
            return Fail("AmbiguousMatch", $"Method '{query}' matched multiple implementations.");
        }

        foreach (var callee in callees)
        {
            await _sessionStore.AddWorkingSetItemAsync(new AddWorkingSetItemRequest(session.Id, callee.CalleeMethodId), cancellationToken);
        }

        return new ProtocolResponse(true, JsonSerializer.SerializeToElement(new CalleesResponse(new CalleeSummaryResponse(query, target is null ? "Unknown" : "Exact", target, callees))), [], []);
    }

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

    private async Task<ProtocolResponse> HandlePatchPreviewAsync(ProtocolRequest request, SessionState session, CancellationToken cancellationToken)
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

        var filesAffected = patch.Operations.Select(operation => operation.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var operationsDetected = patch.Operations.Select(operation => operation.Operation).ToArray();
        var diffPreview = string.Join("\n", patch.Operations.Select(operation => $"--- {operation.Path}\n+++ {operation.Path}\n@@\n+{operation.Content}"));
        var response = new PatchPreviewResponse(true, filesAffected, operationsDetected, diffPreview, []);
        return new ProtocolResponse(true, JsonSerializer.SerializeToElement(response), [], []);
    }

    private async Task<ProtocolResponse> HandlePatchValidateAsync(ProtocolRequest request, SessionState session, CancellationToken cancellationToken)
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

        var warnings = new List<string>();
        var errors = new List<string>();
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenOperations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var operation in patch.Operations)
        {
            files.Add(operation.Path);
            var operationKey = $"{operation.Operation}:{operation.Path}";
            if (!seenOperations.Add(operationKey))
            {
                errors.Add($"Duplicate operation detected for '{operation.Operation}' on '{operation.Path}'.");
            }

            var absolutePath = Path.GetFullPath(Path.Combine(root, operation.Path));
            if (!absolutePath.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Path '{operation.Path}' is outside the repository root.");
            }

            if (operation.Operation.Equals("CreateFile", StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(absolutePath))
            {
                errors.Add($"Target file '{operation.Path}' already exists.");
            }

            if (!operation.Operation.Equals("CreateFile", StringComparison.OrdinalIgnoreCase) && !System.IO.File.Exists(absolutePath))
            {
                errors.Add($"Target file '{operation.Path}' does not exist.");
            }
        }

        var response = new PatchValidateResponse(errors.Count == 0, warnings, errors, files.Count, patch.Operations.Count);
        return new ProtocolResponse(true, JsonSerializer.SerializeToElement(response), [], []);
    }

    private async Task<ProtocolResponse> HandlePatchRollbackAsync(ProtocolRequest request, SessionState session, CancellationToken cancellationToken)
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

        var restoredFiles = 0;
        var revertedOperations = 0;

        foreach (var operation in patch.Operations.Reverse())
        {
            var absolutePath = Path.GetFullPath(Path.Combine(root, operation.Path));
            if (!absolutePath.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            {
                return Fail("ValidationError", $"Path '{operation.Path}' is outside the repository root.");
            }

            if (operation.Operation.Equals("CreateFile", StringComparison.OrdinalIgnoreCase))
            {
                if (!IOFile.Exists(absolutePath))
                {
                    return Fail("NotFound", $"File '{operation.Path}' was not found for rollback.");
                }

                IOFile.Delete(absolutePath);
                restoredFiles++;
                revertedOperations++;
                continue;
            }

            return Fail("ValidationError", $"Rollback does not support operation '{operation.Operation}'.");
        }

        var response = new PatchRollbackResponse(true, restoredFiles, revertedOperations, [], []);
        return new ProtocolResponse(true, JsonSerializer.SerializeToElement(response), [], []);
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
