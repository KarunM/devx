using System.Text.Json;
using System.Text;
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
            "READ_FILE" => await HandleReadFileAsync(request, session, cancellationToken),
            "READ_METHOD" => await HandleReadMethodAsync(request, session, cancellationToken),
            "READ_TYPE" => await HandleReadTypeAsync(request, session, cancellationToken),
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

    private async Task<ProtocolResponse> HandleReadFileAsync(ProtocolRequest request, SessionState session, CancellationToken cancellationToken)
    {
        if (!TryGetQuery(request.Payload, out var path))
        {
            return Fail("ValidationError", "Read file requests must include a string 'query' field.");
        }

        var full = TryGetBoolean(request.Payload, "full");
        var startLine = 0;
        var endLine = 0;
        if (!full && !TryGetRange(request.Payload, out startLine, out endLine))
        {
            return Fail("ValidationError", "Read file requests must include either a 'range' field formatted as 'start:end' or --full.");
        }

        var root = await _repositoryStore.GetRepositoryRootPathAsync(session.RepositoryId, cancellationToken);
        if (string.IsNullOrWhiteSpace(root))
        {
            return Fail("RepositoryNotIndexed", "Repository root was not found.");
        }

        var absolutePath = Path.GetFullPath(Path.Combine(root, path));
        if (!absolutePath.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
        {
            return Fail("ValidationError", "File path must remain inside the repository root.");
        }

        var source = await IOFile.ReadAllTextAsync(absolutePath, cancellationToken);
        var lines = source.Split('\n');
        var safeStart = full ? 1 : Math.Max(1, startLine);
        var safeEnd = full ? lines.Length : Math.Min(endLine, lines.Length);
        if (safeStart > safeEnd)
        {
            return Fail("ValidationError", "Invalid line range.");
        }

        var slice = string.Join('\n', lines[(safeStart - 1)..safeEnd]);
        var response = new SourceReadResponse(
            Kind: "file",
            SymbolId: path,
            QualifiedName: path,
            Path: path,
            StartLine: safeStart,
            EndLine: safeEnd,
            TotalLines: lines.Length,
            ReturnedLines: safeEnd - safeStart + 1,
            Truncated: !full && safeEnd < lines.Length,
            SourceReturned: true,
            Source: slice,
            Metadata: new { Language = Path.GetExtension(path).TrimStart('.').ToUpperInvariant() });
        await RecordReadMetricsAsync(session.Id, "read file", returnedLines: safeEnd - safeStart + 1, cancellationToken);
        return new ProtocolResponse(true, JsonSerializer.SerializeToElement(response), [], []);
    }

    private async Task<ProtocolResponse> HandleReadMethodAsync(ProtocolRequest request, SessionState session, CancellationToken cancellationToken)
    {
        var query = GetReadQuery(request.Payload);
        if (query is null)
        {
            return Fail("ValidationError", "Read method requests must include either a string 'query' field or an 'id' field.");
        }

        var methods = await ResolveMethodSourcesAsync(query, request.Payload, cancellationToken);
        if (methods.Count == 0)
        {
            return Fail("NotFound", $"Method '{query}' was not found.");
        }

        if (methods.Count > 1)
        {
            return Fail("AmbiguousMatch", $"Method '{query}' matched multiple implementations.");
        }

        var method = methods[0];
        var source = await IOFile.ReadAllTextAsync(Path.Combine((await _repositoryStore.GetRepositoryRootPathAsync(session.RepositoryId, cancellationToken))!, method.FilePath), cancellationToken);
        var lines = source.Split('\n');
        var returnedLines = Math.Max(1, method.EndLine - method.StartLine + 1);
        var response = new SourceReadResponse("method", method.Id, method.QualifiedName, method.FilePath, method.StartLine, method.EndLine, lines.Length, returnedLines, false, true, source, new { });
        await _sessionStore.MarkSymbolExpandedAsync(new MarkSymbolExpandedRequest(session.Id, method.Id), cancellationToken);
        await RecordReadMetricsAsync(session.Id, "read method", returnedLines, cancellationToken);
        return new ProtocolResponse(true, JsonSerializer.SerializeToElement(response), [], []);
    }

    private async Task<ProtocolResponse> HandleReadTypeAsync(ProtocolRequest request, SessionState session, CancellationToken cancellationToken)
    {
        var query = GetReadQuery(request.Payload);
        if (query is null)
        {
            return Fail("ValidationError", "Read type requests must include either a string 'query' field or an 'id' field.");
        }

        if (TryGetId(request.Payload, out var id))
        {
            var typeSummary = await _repositoryStore.FindTypeSummaryByIdAsync(id, cancellationToken);
            if (typeSummary is null)
            {
                return Fail("NotFound", $"Type '{id}' was not found.");
            }

            var responseById = new SourceReadResponse("type", typeSummary.Id, typeSummary.QualifiedName, typeSummary.FilePath, typeSummary.StartLine, typeSummary.EndLine, 0, 0, false, false, string.Empty, new { TypeName = typeSummary.Name, Namespace = typeSummary.Namespace, Project = typeSummary.Project, Kind = typeSummary.Kind });
            await _sessionStore.MarkSymbolExpandedAsync(new MarkSymbolExpandedRequest(session.Id, typeSummary.Id), cancellationToken);
            await RecordReadMetricsAsync(session.Id, "read type", 0, cancellationToken);
            return new ProtocolResponse(true, JsonSerializer.SerializeToElement(responseById), [], []);
        }

        var types = await ResolveTypeSummariesAsync(query, request.Payload, cancellationToken);
        if (types.Count == 0)
        {
            return Fail("NotFound", $"Type '{query}' was not found.");
        }

        if (types.Count > 1)
        {
            return Fail("AmbiguousMatch", $"Type '{query}' matched multiple implementations.");
        }

        var type = types[0];
        var response = new SourceReadResponse("type", type.Id, type.QualifiedName, type.FilePath, type.StartLine, type.EndLine, 0, 0, false, false, string.Empty, new { TypeName = type.Name, Namespace = type.Namespace, Project = type.Project, Kind = type.Kind });
        await _sessionStore.MarkSymbolExpandedAsync(new MarkSymbolExpandedRequest(session.Id, type.Id), cancellationToken);
        await RecordReadMetricsAsync(session.Id, "read type", 0, cancellationToken);
        return new ProtocolResponse(true, JsonSerializer.SerializeToElement(response), [], []);
    }

    private async Task RecordReadMetricsAsync(SessionId sessionId, string operation, int returnedLines, CancellationToken cancellationToken)
    {
        await _sessionStore.AddOperationMetricAsync(new OperationMetric(operation, DateTimeOffset.UtcNow, TimeSpan.Zero, true, sessionId.Value.ToString()), cancellationToken);
        await _sessionStore.AddOperationMetricAsync(new OperationMetric("read source lines", DateTimeOffset.UtcNow, TimeSpan.Zero, true, sessionId.Value.ToString()), cancellationToken);
    }

    private static string? GetReadQuery(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (payload.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String)
        {
            return idElement.GetString();
        }

        if (payload.TryGetProperty("query", out var queryElement) && queryElement.ValueKind == JsonValueKind.String)
        {
            return queryElement.GetString();
        }

        return null;
    }

    private async Task<IReadOnlyList<MethodSource>> ResolveMethodSourcesAsync(string query, JsonElement payload, CancellationToken cancellationToken)
    {
        if (TryGetId(payload, out var id))
        {
            var methods = await _repositoryStore.FindMethodSourcesAsync(id, cancellationToken);
            return methods.Where(method => string.Equals(method.Id, id, StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        return await _repositoryStore.FindMethodSourcesAsync(query, cancellationToken);
    }

    private async Task<IReadOnlyList<TypeSummary>> ResolveTypeSummariesAsync(string query, JsonElement payload, CancellationToken cancellationToken)
    {
        if (TryGetId(payload, out var id))
        {
            query = id;
        }

        return await _repositoryStore.FindTypeSummariesAsync(query, cancellationToken);
    }

    private static bool TryGetId(JsonElement payload, out string id)
    {
        id = string.Empty;
        return payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("id", out var idElement)
            && idElement.ValueKind == JsonValueKind.String
            && (id = idElement.GetString() ?? string.Empty).Length > 0;
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

        var warnings = new List<ProtocolDiagnostic>();
        var validation = ValidatePatch(root, patch, warnings);
        var filesAffected = patch.Operations.Select(operation => operation.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var operationsDetected = patch.Operations.Select(operation => operation.Operation).ToArray();
        var diffPreview = string.Join("\n", patch.Operations.Select(operation => BuildPreview(operation)));
        var response = new PatchPreviewResponse(validation.Count == 0, filesAffected, operationsDetected, diffPreview, validation);
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

        var validation = ValidatePatch(root, patch, []);
        var files = patch.Operations.Select(operation => operation.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var warnings = validation.Where(error => error.Code == "Warning").Select(error => error.Message).ToArray();
        var errors = validation.Where(error => error.Code != "Warning").Select(error => error.Message).ToArray();
        var response = new PatchValidateResponse(errors.Length == 0, warnings, errors, files, patch.Operations.Count);
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

            if (operation.Operation.Equals("DeleteFile", StringComparison.OrdinalIgnoreCase))
            {
                return Fail("ValidationError", "Rollback for DeleteFile requires a backup and is unsupported here.");
            }

            if (operation.Operation.Equals("ReplaceMethod", StringComparison.OrdinalIgnoreCase)
                || operation.Operation.Equals("InsertMethod", StringComparison.OrdinalIgnoreCase)
                || operation.Operation.Equals("AddUsing", StringComparison.OrdinalIgnoreCase))
            {
                return Fail("ValidationError", $"Rollback does not support operation '{operation.Operation}' without stored before-state.");
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

        if (IsUnsafeUpdateFile(operation, targetPath))
        {
            return (false, "UnsafeOperation", "Whole-file replacement is not permitted for source files.");
        }

        return operation.Operation switch
        {
            "CreateFile" => ApplyCreateFile(targetPath, operation.Content),
            "DeleteFile" => ApplyDeleteFile(targetPath),
            "ReplaceMethod" => ApplyReplaceMethod(targetPath, operation.MethodName, operation.ReplacementText),
            "InsertMethod" => ApplyInsertMethod(targetPath, operation.ContainingType, operation.TypeId, operation.MethodText, operation.Position),
            "AddUsing" => ApplyAddUsing(targetPath, operation.Namespace),
            "InsertText" => ApplyInsertText(targetPath, operation.Content, operation.Position),
            "ReplaceLineRange" => ApplyReplaceLineRange(targetPath, operation.Content, operation.Position),
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
        var methodPattern = $@"(?m)^[ \t]*(?:public|private|protected|internal|static|async|sealed|override|partial|virtual|extern|unsafe|new|readonly|ref|internal\s+static|private\s+static|public\s+static|protected\s+static)[^\n]*\b{System.Text.RegularExpressions.Regex.Escape(methodName)}\b[^\n]*$";
        var signatureMatches = System.Text.RegularExpressions.Regex.Matches(text, methodPattern);
        if (signatureMatches.Count != 1)
        {
            return (false, "ValidationError", "Target method must be unambiguous.");
        }

        var signatureMatch = signatureMatches[0];
        var bodyStart = text.IndexOf('{', signatureMatch.Index + signatureMatch.Length);
        if (bodyStart < 0)
        {
            return (false, "ValidationError", "Target method body was not found.");
        }

        var bodyEnd = FindMatchingBrace(text, bodyStart);
        if (bodyEnd < 0)
        {
            return (false, "ValidationError", "Target method body was not balanced.");
        }

        var matchStart = signatureMatch.Index;
        var matchLength = bodyEnd + 1 - matchStart;
        var updated = text[..matchStart] + replacementText + text[(matchStart + matchLength)..];
        IOFile.WriteAllText(targetPath, updated);
        return (true, string.Empty, string.Empty);
    }

    private static int FindMatchingBrace(string text, int openBraceIndex)
    {
        var depth = 0;
        for (var index = openBraceIndex; index < text.Length; index++)
        {
            if (text[index] == '{')
            {
                depth++;
            }
            else if (text[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return index;
                }
            }
        }

        return -1;
    }

    private static (bool Success, string Code, string Message) ApplyInsertMethod(string targetPath, string? containingType, string? typeId, string? methodText, string? position)
    {
        if (!IOFile.Exists(targetPath))
        {
            return (false, "NotFound", "Target file does not exist.");
        }

        if (string.IsNullOrWhiteSpace(methodText) || string.IsNullOrWhiteSpace(position))
        {
            return (false, "ValidationError", "InsertMethod requires containingType or typeId, methodText, and position.");
        }

        var text = IOFile.ReadAllText(targetPath);
        var typeMatch = !string.IsNullOrWhiteSpace(containingType)
            ? System.Text.RegularExpressions.Regex.Match(text, $@"class\s+{System.Text.RegularExpressions.Regex.Escape(containingType)}[\s\S]*?\{{")
            : typeId is null ? System.Text.RegularExpressions.Regex.Match(text, @"class\s+[A-Za-z_][A-Za-z0-9_]*[\s\S]*?\{") : System.Text.RegularExpressions.Regex.Match(text, @"class\s+[A-Za-z_][A-Za-z0-9_]*[\s\S]*?\{");

        if (!typeMatch.Success)
        {
            return (false, "ValidationError", "Target type must exist and be unambiguous.");
        }

        var insertAt = position.StartsWith("Before:", StringComparison.OrdinalIgnoreCase) || position.StartsWith("After:", StringComparison.OrdinalIgnoreCase)
            ? text.IndexOf(position[(position.IndexOf(':') + 1)..], StringComparison.Ordinal)
            : text.LastIndexOf('}');

        if (insertAt < 0)
        {
            return (false, "ValidationError", "Insertion point was not found.");
        }

        var updated = position.StartsWith("Before:", StringComparison.OrdinalIgnoreCase)
            ? text[..insertAt] + methodText + Environment.NewLine + text[insertAt..]
            : position.StartsWith("After:", StringComparison.OrdinalIgnoreCase)
                ? text[..(insertAt + position[(position.IndexOf(':') + 1)..].Length)] + Environment.NewLine + methodText + text[(insertAt + position[(position.IndexOf(':') + 1)..].Length)..]
                : text[..insertAt] + Environment.NewLine + methodText + Environment.NewLine + text[insertAt..];

        IOFile.WriteAllText(targetPath, updated);
        return (true, string.Empty, string.Empty);
    }

    private static (bool Success, string Code, string Message) ApplyAddUsing(string targetPath, string? @namespace)
    {
        if (!IOFile.Exists(targetPath))
        {
            return (false, "NotFound", "Target file does not exist.");
        }

        if (string.IsNullOrWhiteSpace(@namespace))
        {
            return (false, "ValidationError", "AddUsing requires namespace.");
        }

        var text = IOFile.ReadAllText(targetPath);
        var usingLine = $"using {@namespace};";
        if (text.Contains(usingLine, StringComparison.Ordinal))
        {
            return (false, "ValidationError", "Duplicate using is not allowed.");
        }

        var insertAt = text.StartsWith("using ", StringComparison.Ordinal) ? text.LastIndexOf("using ", StringComparison.OrdinalIgnoreCase) : 0;
        var updated = insertAt <= 0
            ? usingLine + Environment.NewLine + text
            : text.Insert(insertAt, usingLine + Environment.NewLine);

        IOFile.WriteAllText(targetPath, updated);
        return (true, string.Empty, string.Empty);
    }

    private static (bool Success, string Code, string Message) ApplyInsertText(string targetPath, string? content, string? position)
    {
        if (!IOFile.Exists(targetPath))
        {
            return (false, "NotFound", "Target file does not exist.");
        }

        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(position))
        {
            return (false, "ValidationError", "InsertText requires Content and Position (e.g., 'after:42').");
        }

        var lines = IOFile.ReadAllLines(targetPath);
        var match = System.Text.RegularExpressions.Regex.Match(position, @"(after|before):(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success || !int.TryParse(match.Groups[2].Value, out var lineNumber) || lineNumber < 1 || lineNumber > lines.Length)
        {
            return (false, "ValidationError", "Position must be 'after:N' or 'before:N' where N is a valid line number.");
        }

        var insertIndex = match.Groups[1].Value.Equals("after", StringComparison.OrdinalIgnoreCase) ? lineNumber : lineNumber - 1;
        var contentLines = content.Split('\n');
        var newLines = lines.Take(insertIndex).Concat(contentLines).Concat(lines.Skip(insertIndex)).ToArray();
        IOFile.WriteAllLines(targetPath, newLines);
        return (true, string.Empty, string.Empty);
    }

    private static (bool Success, string Code, string Message) ApplyReplaceLineRange(string targetPath, string? content, string? position)
    {
        if (!IOFile.Exists(targetPath))
        {
            return (false, "NotFound", "Target file does not exist.");
        }

        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(position))
        {
            return (false, "ValidationError", "ReplaceLineRange requires Content and Position (e.g., '10:15').");
        }

        var rangeMatch = System.Text.RegularExpressions.Regex.Match(position, @"(\d+):(\d+)");
        if (!rangeMatch.Success || !int.TryParse(rangeMatch.Groups[1].Value, out var startLine) || !int.TryParse(rangeMatch.Groups[2].Value, out var endLine))
        {
            return (false, "ValidationError", "Position must be 'start:end' line range.");
        }

        var lines = IOFile.ReadAllLines(targetPath);
        if (startLine < 1 || endLine > lines.Length || startLine > endLine)
        {
            return (false, "ValidationError", "Invalid line range.");
        }

        var contentLines = content.Split('\n');
        var newLines = lines.Take(startLine - 1).Concat(contentLines).Concat(lines.Skip(endLine)).ToArray();
        IOFile.WriteAllLines(targetPath, newLines);
        return (true, string.Empty, string.Empty);
    }

    private static IReadOnlyList<ProtocolDiagnostic> ValidatePatch(string root, PatchDocument patch, List<ProtocolDiagnostic> warnings)
    {
        var normalizedRoot = Path.GetFullPath(root);
        var errors = new List<ProtocolDiagnostic>();
        var seenOperations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var operation in patch.Operations)
        {
            var operationKey = $"{operation.Operation}:{operation.Path}";
            if (!seenOperations.Add(operationKey))
            {
                errors.Add(new ProtocolDiagnostic("ValidationError", $"Duplicate operation detected for '{operation.Operation}' on '{operation.Path}'."));
            }

            var absolutePath = Path.GetFullPath(Path.Combine(normalizedRoot, operation.Path));
            if (!absolutePath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new ProtocolDiagnostic("ValidationError", $"Path '{operation.Path}' is outside the repository root."));
                continue;
            }

            if (IsUnsafeUpdateFile(operation, absolutePath))
            {
                errors.Add(new ProtocolDiagnostic("ValidationError", "Whole-file replacement is not permitted for source files."));
            }

            if (operation.Operation.Equals("CreateFile", StringComparison.OrdinalIgnoreCase) && IOFile.Exists(absolutePath))
            {
                errors.Add(new ProtocolDiagnostic("ValidationError", $"Target file '{operation.Path}' already exists."));
            }

            if (!operation.Operation.Equals("CreateFile", StringComparison.OrdinalIgnoreCase) && !IOFile.Exists(absolutePath))
            {
                errors.Add(new ProtocolDiagnostic("ValidationError", $"Target file '{operation.Path}' does not exist."));
            }

            if (operation.Operation.Equals("ReplaceMethod", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(operation.ReplacementText))
            {
                errors.Add(new ProtocolDiagnostic("ValidationError", "ReplaceMethod requires replacementText."));
            }

            if (operation.Operation.Equals("InsertMethod", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(operation.MethodText))
            {
                errors.Add(new ProtocolDiagnostic("ValidationError", "InsertMethod requires methodText."));
            }

            if (operation.Operation.Equals("AddUsing", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(operation.Namespace))
            {
                errors.Add(new ProtocolDiagnostic("ValidationError", "AddUsing requires namespace."));
            }

            if (operation.Operation.Equals("UpdateFile", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new ProtocolDiagnostic("ValidationError", "UpdateFile remains blocked for source files."));
            }
        }

        return errors;
    }

    private static string BuildPreview(PatchOperation operation)
        => operation.Operation switch
        {
            "CreateFile" => $"--- /dev/null\n+++ {operation.Path}\n@@\n+{operation.Content}",
            "DeleteFile" => $"--- {operation.Path}\n+++ /dev/null\n@@",
            "ReplaceMethod" => $"--- {operation.Path}\n+++ {operation.Path}\n@@\n-{operation.MethodName}\n+{operation.ReplacementText}",
            "InsertMethod" => $"--- {operation.Path}\n+++ {operation.Path}\n@@\n+{operation.MethodText}",
            "AddUsing" => $"--- {operation.Path}\n+++ {operation.Path}\n@@\n+using {operation.Namespace};",
            _ => $"--- {operation.Path}\n+++ {operation.Path}\n@@\n+unsupported operation {operation.Operation}"
        };

    private static ProtocolResponse Fail(string code, string message)
        => new(false, null, [], [new ProtocolError(code, message)]);

    private static bool IsUnsafeUpdateFile(PatchOperation operation, string targetPath)
        => operation.Operation.Equals("UpdateFile", StringComparison.OrdinalIgnoreCase)
            && (targetPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || targetPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                || targetPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase));

    private static SessionId ParseSessionId(string sessionId) => new(Guid.Parse(sessionId));

    private static bool TryGetQuery(JsonElement payload, out string query)
    {
        query = string.Empty;
        return payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("query", out var queryElement)
            && queryElement.ValueKind == JsonValueKind.String
            && (query = queryElement.GetString() ?? string.Empty).Length > 0;
    }

    private static bool TryGetRange(JsonElement payload, out int startLine, out int endLine)
    {
        startLine = 0;
        endLine = 0;
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("range", out var rangeElement) || rangeElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var range = rangeElement.GetString();
        if (string.IsNullOrWhiteSpace(range))
        {
            return false;
        }

        var parts = range.Split(':', 2);
        return parts.Length == 2
            && int.TryParse(parts[0], out startLine)
            && int.TryParse(parts[1], out endLine)
            && startLine > 0
            && endLine >= startLine;
    }

    private static bool TryGetBoolean(JsonElement payload, string propertyName)
        => payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty(propertyName, out var element)
            && element.ValueKind == JsonValueKind.True;
}
