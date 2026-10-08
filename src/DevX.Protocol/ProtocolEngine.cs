using System.Text.Json;
using System.Text;
using DevX.Application;
using DevX.Domain;
using DevX.Indexer;
using IOFile = System.IO.File;

namespace DevX.Protocol;

public sealed class ProtocolEngine : IProtocolEngine
{
    private readonly ISearchService _searchService;
    private readonly ISessionStore _sessionStore;
    private readonly IRepositoryStore _repositoryStore;
    private readonly DotnetBuildTestService _buildTestService;
    private readonly PatchLifecycleService _patchLifecycleService;
    private readonly RuntimeContextResolver _runtimeContextResolver;

    public ProtocolEngine(ISearchService searchService, ISessionStore sessionStore, IRepositoryStore repositoryStore)
        : this(searchService, sessionStore, repositoryStore, new DotnetBuildTestService())
    {
    }

    public ProtocolEngine(ISearchService searchService, ISessionStore sessionStore, IRepositoryStore repositoryStore, DotnetBuildTestService buildTestService)
        : this(searchService, sessionStore, repositoryStore, buildTestService, new PatchLifecycleService())
    {
    }

    public ProtocolEngine(ISearchService searchService, ISessionStore sessionStore, IRepositoryStore repositoryStore, DotnetBuildTestService buildTestService, PatchLifecycleService patchLifecycleService)
    {
        _searchService = searchService;
        _sessionStore = sessionStore;
        _repositoryStore = repositoryStore;
        _buildTestService = buildTestService;
        _patchLifecycleService = patchLifecycleService;
        _runtimeContextResolver = new RuntimeContextResolver();
    }

    public async Task<ProtocolResponse> ExecuteAsync(ProtocolRequest request, CancellationToken cancellationToken = default)
    {
        var readStarted = DateTimeOffset.UtcNow;
        if (!string.Equals(request.Version, "1.0", StringComparison.Ordinal))
        {
            return Fail("UnsupportedVersion", $"Unsupported protocol version '{request.Version}'.");
        }

        var session = await _sessionStore.GetSessionAsync(new GetSessionRequest(ParseSessionId(request.SessionId)), cancellationToken);
        if (session is null)
        {
            return Fail("SessionNotFound", $"Session '{request.SessionId}' was not found.");
        }

        try
        {
            var response = request.Operation switch
            {
                ProtocolOperations.SearchSymbol => await HandleSearchSymbolAsync(request, session, cancellationToken),
                ProtocolOperations.SearchText => await HandleSearchTextAsync(request, session, cancellationToken),
                ProtocolOperations.GetFileSnapshot => await HandleGetFileSnapshotAsync(request, session, cancellationToken),
                ProtocolOperations.GetMethodSummary => await HandleGetMethodSummaryAsync(request, session, cancellationToken),
                ProtocolOperations.GetMethod => await HandleGetMethodAsync(request, session, cancellationToken),
                "READ_FILE" => await HandleReadFileAsync(request, session, cancellationToken),
                "READ_METHOD" => await HandleReadMethodAsync(request, session, cancellationToken),
                "READ_SECTION" => await HandleReadSectionAsync(request, session, cancellationToken),
                "READ_TYPE" => await HandleReadTypeAsync(request, session, cancellationToken),
                ProtocolOperations.GetTypeOperation => await HandleGetTypeAsync(request, session, cancellationToken),
                ProtocolOperations.GetCallers => await HandleGetCallersAsync(request, session, cancellationToken),
                ProtocolOperations.GetCallees => await HandleGetCalleesAsync(request, session, cancellationToken),
                ProtocolOperations.GetReferences => await HandleGetReferencesAsync(request, session, cancellationToken),
                ProtocolOperations.Patch => await HandlePatchAsync(request, session, cancellationToken),
                "PATCH_PREVIEW" => await HandlePatchPreviewAsync(request, session, cancellationToken),
                "PATCH_VALIDATE" => await HandlePatchValidateAsync(request, session, cancellationToken),
                ProtocolOperations.PatchRollback => await HandlePatchRollbackAsync(request, session, cancellationToken),
                ProtocolOperations.RunBuild => await HandleRunBuildAsync(request, session, cancellationToken),
                ProtocolOperations.RunTests => await HandleRunTestsAsync(request, session, cancellationToken),
                ProtocolOperations.GetDiagnostics => await HandleGetDiagnosticsAsync(session, cancellationToken),
                ProtocolOperations.GetMethod or ProtocolOperations.GetCallers or ProtocolOperations.GetCallees or ProtocolOperations.GetTests
                    => Fail("HandlerNotImplemented", $"Operation '{request.Operation}' is not implemented yet."),
                _ => Fail("InvalidOperation", $"Unknown operation '{request.Operation}'.")
            };
            if (response.Success && request.Operation.StartsWith("READ_", StringComparison.Ordinal)
                && response.Data is { ValueKind: JsonValueKind.Object } data && data.TryGetProperty("Path", out var path))
            {
                var root = await _repositoryStore.GetRepositoryRootPathAsync(session.RepositoryId, cancellationToken);
                if (root is not null)
                {
                    var absolute = Path.GetFullPath(Path.Combine(root, path.GetString()!));
                    if (!IOFile.Exists(absolute)) return Fail("FileMissing", $"file missing: {path.GetString()}", GetReadQuery(request.Payload));
                    var whole = await IOFile.ReadAllTextAsync(absolute, cancellationToken);
                    var node = System.Text.Json.Nodes.JsonNode.Parse(data.GetRawText())!.AsObject();
                    var metadata = node["Metadata"] as System.Text.Json.Nodes.JsonObject ?? new();
                    metadata["WholeFileCharacters"] = whole.Length;
                    metadata["ReturnedCharacters"] = data.TryGetProperty("SourceReturned", out var sourceReturned) && sourceReturned.GetBoolean()
                        ? data.GetProperty("Source").GetString()!.Length : data.GetRawText().Length;
                    metadata["ReadEventId"] = Guid.NewGuid().ToString();
                    metadata["ReturnedLines"] = data.GetProperty("ReturnedLines").GetInt32();
                    node["Metadata"] = metadata;
                    response = response with { Data = JsonSerializer.SerializeToElement(node) };
                    var metricMetadata = new System.Text.Json.Nodes.JsonObject();
                    foreach (var entry in metadata) metricMetadata[entry.Key] = entry.Value?.DeepClone();
                    metricMetadata["EventOrigin"] = "internal-read";
                    await _sessionStore.AddOperationMetricAsync(new OperationMetric(request.Operation.ToLowerInvariant().Replace('_', ' '),
                        readStarted, DateTimeOffset.UtcNow - readStarted, true, session.Id.Value.ToString(), metricMetadata.ToJsonString(), request.AgentLabel), cancellationToken);
                }
            }
            return response;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return Fail("FileMissing", $"file missing: {error.Message}", GetReadQuery(request.Payload));
        }
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

        var methods = await LookupMethodSourcesAsync(query, cancellationToken);
        methods = ResolveSafeMethodMatches(methods, query);
        if (methods.Count == 0)
        {
            return await NotFoundWithSuggestionsAsync("Method", query, cancellationToken);
        }

        if (methods.Count > 1)
        {
            return Fail("AmbiguousMatch", $"Method '{query}' matched multiple implementations.", query, methods.Select(method => method.QualifiedName));
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
        types = PreferExact(types, query, type => type.Id, type => type.QualifiedName, type => type.Name);
        if (types.Count == 0)
        {
            return await NotFoundWithSuggestionsAsync("Type", query, cancellationToken);
        }

        if (types.Count > 1)
        {
            return Fail("AmbiguousMatch", $"Type '{query}' matched multiple implementations.", query, types.Select(type => type.QualifiedName));
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

        var methods = await _repositoryStore.FindMethodSummariesAsync(query, cancellationToken);
        var unsupported = UnsupportedPythonGraphResponse(query, methods);
        if (unsupported is not null)
        {
            return unsupported;
        }

        var callers = await _repositoryStore.FindCallersAsync(query, cancellationToken);
        if (callers.Count == 0)
        {
            return Fail("NotFound", $"Method '{query}' was not found.");
        }

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

        var methodMatches = await _repositoryStore.FindMethodSummariesAsync(query, cancellationToken);
        var unsupported = UnsupportedPythonGraphResponse(query, methodMatches);
        if (unsupported is not null)
        {
            return unsupported;
        }

        var symbolMatches = await _searchService.SearchSymbolAsync(new SearchSymbolRequest(query), cancellationToken);
        if (symbolMatches.Symbols.Any(symbol => symbol.FilePath?.EndsWith(".py", StringComparison.OrdinalIgnoreCase) == true))
        {
            return Fail("UnsupportedLanguage", $"References are not supported for Python symbol '{query}'.");
        }

        var references = await _repositoryStore.FindReferencesAsync(query, cancellationToken);
        if (references.Count == 0)
        {
            return Fail("NotFound", $"Symbol '{query}' was not found.");
        }

        var targetSearch = methodMatches;

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

        var methods = await _repositoryStore.FindMethodSummariesAsync(query, cancellationToken);
        var unsupported = UnsupportedPythonGraphResponse(query, methods);
        if (unsupported is not null)
        {
            return unsupported;
        }

        var callees = await _repositoryStore.FindCalleesAsync(query, cancellationToken);
        if (callees.Count == 0)
        {
            return Fail("NotFound", $"Method '{query}' was not found.");
        }

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

    private static ProtocolResponse? UnsupportedPythonGraphResponse(string query, IReadOnlyList<MethodSummary> methods)
        => methods.Any(method => method.FilePath.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
            ? Fail("UnsupportedLanguage", $"Call graph queries are not supported for Python symbol '{query}'.")
            : null;

    private async Task<ProtocolResponse> HandleRunBuildAsync(ProtocolRequest request, SessionState session, CancellationToken cancellationToken)
    {
        var root = await _repositoryStore.GetRepositoryRootPathAsync(session.RepositoryId, cancellationToken);
        if (string.IsNullOrWhiteSpace(root))
        {
            return Fail("RepositoryNotIndexed", "Repository root was not found.");
        }

        var resolution = ResolveValidationContext(root, request.Payload, ValidationMode.Build);
        if (!resolution.Success || resolution.Context is null || resolution.SelectedTarget is null)
        {
            return Fail("ValidationError", resolution.Errors.FirstOrDefault() ?? "Runtime target resolution failed.");
        }

        var execution = await _buildTestService.RunBuildAsync(root, resolution.TargetSelector, cancellationToken);
        await _sessionStore.AddExecutionAsync(new AddExecutionRequest(session.Id, execution), cancellationToken);
        foreach (var diagnostic in execution.Diagnostics)
        {
            await _sessionStore.AddDiagnosticAsync(new AddDiagnosticRequest(session.Id, diagnostic), cancellationToken);
        }

        var response = new ValidationExecutionResponse("build", resolution.Context, resolution.SelectedTarget, execution);
        return new ProtocolResponse(execution.Success, JsonSerializer.SerializeToElement(response), [], execution.Success ? [] : [new ProtocolError("ValidationError", "Build failed.")]);
    }

    private async Task<ProtocolResponse> HandleRunTestsAsync(ProtocolRequest request, SessionState session, CancellationToken cancellationToken)
    {
        var root = await _repositoryStore.GetRepositoryRootPathAsync(session.RepositoryId, cancellationToken);
        if (string.IsNullOrWhiteSpace(root))
        {
            return Fail("RepositoryNotIndexed", "Repository root was not found.");
        }

        var resolution = ResolveValidationContext(root, request.Payload, ValidationMode.Test);
        if (!resolution.Success || resolution.Context is null || resolution.SelectedTarget is null)
        {
            return Fail("ValidationError", resolution.Errors.FirstOrDefault() ?? "Runtime target resolution failed.");
        }

        var targets = ReadStringArray(request.Payload, "targets");
        if (targets.Count == 0 && !string.IsNullOrWhiteSpace(resolution.TargetSelector))
        {
            targets = [resolution.TargetSelector];
        }
        var timeoutSeconds = ReadPositiveInt(request.Payload, "timeoutSeconds") ?? 300;
        var testFilter = request.Payload.ValueKind == JsonValueKind.Object && request.Payload.TryGetProperty("filter", out var filterElement) && filterElement.ValueKind == JsonValueKind.String ? filterElement.GetString() : null;
        var execution = await _buildTestService.RunTestsAsync(root, targets, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken, testFilter);
        await _sessionStore.AddExecutionAsync(new AddExecutionRequest(session.Id, execution), cancellationToken);
        foreach (var diagnostic in execution.Diagnostics)
        {
            await _sessionStore.AddDiagnosticAsync(new AddDiagnosticRequest(session.Id, diagnostic), cancellationToken);
        }

        var response = new ValidationExecutionResponse("test", resolution.Context, resolution.SelectedTarget, execution);
        return new ProtocolResponse(execution.Success, JsonSerializer.SerializeToElement(response), [], execution.Success ? [] : [new ProtocolError("ValidationError", "Tests failed.")]);
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

        if (!IOFile.Exists(absolutePath)) return Fail("FileMissing", $"file missing: {path}", path);
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
        return new ProtocolResponse(true, JsonSerializer.SerializeToElement(response), [], []);
    }

    private async Task<ProtocolResponse> HandleReadMethodAsync(ProtocolRequest request, SessionState session, CancellationToken cancellationToken)
    {
        var query = GetReadQuery(request.Payload);
        if (query is null)
        {
            return Fail("ValidationError", "Read method requests must include either a string 'query' field or an 'id' field.");
        }

        if (_repositoryStore is ISymbolStore)
        {
            var root = await _repositoryStore.GetRepositoryRootPathAsync(session.RepositoryId, cancellationToken);
            if (root is not null)
            {
                var resolution = await ReadableSymbolResolver.ResolveAsync(_repositoryStore, root, query, cancellationToken);
                if (resolution.Candidates.Count > 1)
                {
                    var candidates = resolution.Candidates;
                    return new ProtocolResponse(false, null, [], [new ProtocolError("AmbiguousMatch",
                        $"Symbol '{query}' matched multiple definitions: " + string.Join("; ", candidates.Select(s => $"{s.QualifiedName} ({s.Kind}, {s.FilePath}:{s.StartLine}-{s.EndLine})")),
                        query, candidates.Select(s => s.QualifiedName).ToList(), candidates)]);
                }
                if (resolution.Candidates.Count == 1)
                {
                    var symbol = resolution.Candidates[0];
                    var absolute = Path.GetFullPath(Path.Combine(root, symbol.FilePath!));
                    if (!IOFile.Exists(absolute)) return Fail("FileMissing", $"file missing: {symbol.FilePath}", query);
                    if (symbol.StartLine is not null && symbol.EndLine is not null)
                    {
                        var lines = (await IOFile.ReadAllTextAsync(absolute, cancellationToken)).Replace("\r\n", "\n").Split('\n');
                        if (symbol.StartLine < 1 || symbol.EndLine > lines.Length) return Fail("StaleIndex", $"Indexed span is stale for {symbol.FilePath}; run devx index update.", query);
                        var source = string.Join('\n', lines[(symbol.StartLine.Value - 1)..symbol.EndLine.Value]);
                        var symbolId = symbol.Id;
                        // Preserve established method source/identity contracts after the new resolver
                        // has constrained the target. Never accept source from a different workspace.
                        if (symbol.Kind is "Method" or "Constructor")
                        {
                            var legacy = (await _repositoryStore.FindMethodSourcesAsync(symbol.QualifiedName, cancellationToken))
                                .Where(method => method.FilePath == symbol.FilePath && method.QualifiedName == symbol.QualifiedName)
                                .DistinctBy(method => method.Id).ToList();
                            if (legacy.Count == 1 && string.Join('\n', lines).Contains(legacy[0].Source.Replace("\r\n", "\n"), StringComparison.Ordinal))
                            {
                                symbolId = legacy[0].Id;
                                source = legacy[0].Source.Replace("\r\n", "\n");
                            }
                        }
                        var kind = Path.GetExtension(symbol.FilePath!).Equals(".md", StringComparison.OrdinalIgnoreCase) ? "section" : symbol.Kind is "Method" or "Constructor" ? "method" : symbol.Kind.ToLowerInvariant();
                        var message = symbol.Kind is "Method" or "Constructor" ? null : $"{query} is a {symbol.Kind}; returned its definition";
                        var symbolReturnedLines = source.Split('\n').Length;
                        var symbolTotalLines = Math.Max(lines.Length - (lines[^1].Length == 0 ? 1 : 0), symbolReturnedLines);
                        var result = new SourceReadResponse(kind, symbolId, symbol.QualifiedName, symbol.FilePath!, symbol.StartLine.Value,
                            symbol.EndLine.Value, symbolTotalLines, symbolReturnedLines, symbolReturnedLines < symbolTotalLines, true, source,
                            new { ResolvedVia = resolution.ResolvedVia, SymbolKind = kind == "section" ? "Section" : symbol.Kind, Message = message });
                        await _sessionStore.MarkSymbolExpandedAsync(new MarkSymbolExpandedRequest(session.Id, symbolId), cancellationToken);
                        return new ProtocolResponse(true, JsonSerializer.SerializeToElement(result), [], []);
                    }
                }
                // Legacy source IDs use a different identity from indexed symbols; retain ID support.
                if (resolution.Candidates.Count == 0 && !Guid.TryParse(query, out _))
                {
                    if (query.Contains('.')) return Fail("NotFound", $"Symbol '{query}' is not in this workspace: {root}.", query, []);
                    return await NotFoundWithSuggestionsAsync("Symbol", query, cancellationToken);
                }
            }
        }
        var methods = await ResolveMethodSourcesAsync(query, request.Payload, cancellationToken);
        methods = ResolveSafeMethodMatches(methods, query);
        if (methods.Count == 0)
        {
            return await NotFoundWithSuggestionsAsync("Method", query, cancellationToken);
        }

        if (methods.Count > 1)
        {
            return Fail("AmbiguousMatch", $"Method '{query}' matched multiple implementations.", query, methods.Select(method => method.QualifiedName));
        }

        var method = methods[0];
        var slice = method.Source.Replace("\r\n", "\n");
        var returnedLines = slice.Split('\n').Length;
        var snapshot = await _repositoryStore.GetFileSnapshotAsync(method.FilePath, cancellationToken);
        var totalLines = Math.Max(snapshot?.ApproximateLoc ?? method.EndLine, returnedLines);
        var response = new SourceReadResponse("method", method.Id, method.QualifiedName, method.FilePath, method.StartLine, method.EndLine, totalLines, returnedLines, returnedLines < totalLines, true, slice, new { ResolvedVia = Guid.TryParse(query, out _) ? "exact" : "short-name" });
        await _sessionStore.MarkSymbolExpandedAsync(new MarkSymbolExpandedRequest(session.Id, method.Id), cancellationToken);
        return new ProtocolResponse(true, JsonSerializer.SerializeToElement(response), [], []);
    }

    private async Task<ProtocolResponse> HandleReadTypeAsync(ProtocolRequest request, SessionState session, CancellationToken cancellationToken)
    {
        var query = GetReadQuery(request.Payload);
        if (query is null)
        {
            return Fail("ValidationError", "Read type requests must include either a string 'query' field or an 'id' field.");
        }

        if (_repositoryStore is ISymbolStore)
        {
            var typeRoot = await _repositoryStore.GetRepositoryRootPathAsync(session.RepositoryId, cancellationToken);
            if (typeRoot is not null)
            {
                var indexed = await ReadableSymbolResolver.ResolveAsync(_repositoryStore, typeRoot, query, cancellationToken);
                if (indexed.Candidates.Count == 1 && indexed.Candidates[0].FileStatus == "file missing")
                    return Fail("FileMissing", $"file missing: {indexed.Candidates[0].FilePath}", query);
            }
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
            return new ProtocolResponse(true, JsonSerializer.SerializeToElement(responseById), [], []);
        }

        var types = await ResolveTypeSummariesAsync(query, request.Payload, cancellationToken);
        types = PreferExact(types, query, type => type.Id, type => type.QualifiedName, type => type.Name);
        if (types.Count == 0)
        {
            return await NotFoundWithSuggestionsAsync("Type", query, cancellationToken);
        }

        if (types.Count > 1)
        {
            return Fail("AmbiguousMatch", $"Type '{query}' matched multiple implementations.", query, types.Select(type => type.QualifiedName));
        }

        var type = types[0];
        var response = new SourceReadResponse("type", type.Id, type.QualifiedName, type.FilePath, type.StartLine, type.EndLine, 0, 0, false, false, string.Empty, new { TypeName = type.Name, Namespace = type.Namespace, Project = type.Project, Kind = type.Kind });
        await _sessionStore.MarkSymbolExpandedAsync(new MarkSymbolExpandedRequest(session.Id, type.Id), cancellationToken);
        return new ProtocolResponse(true, JsonSerializer.SerializeToElement(response), [], []);
    }

    private async Task<ProtocolResponse> HandleReadSectionAsync(ProtocolRequest request, SessionState session, CancellationToken cancellationToken)
    {
        if (!TryGetString(request.Payload, "file", out var file) || !TryGetString(request.Payload, "heading", out var heading))
        {
            return Fail("ValidationError", "Read section requests must use 'file#heading'.");
        }

        var sectionRoot = await _repositoryStore.GetRepositoryRootPathAsync(session.RepositoryId, cancellationToken);
        if (sectionRoot is not null && !IOFile.Exists(Path.Combine(sectionRoot, file)))
            return Fail("FileMissing", $"file missing: {file}", $"{file}#{heading}");

        var snapshot = await _repositoryStore.GetFileSnapshotAsync(file, cancellationToken);
        if (snapshot is null)
        {
            return Fail("NotFound", $"Markdown file '{file}' was not found.", file);
        }

        var exactCandidates = snapshot.Methods
            .Where(method => string.Equals(method.Name, heading, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var candidates = exactCandidates.Count > 0
            ? exactCandidates
            : snapshot.Methods.Where(method => method.Name.StartsWith(heading, StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0)
        {
            var suggestions = snapshot.Methods
                .Where(method => method.Name.Contains(heading, StringComparison.OrdinalIgnoreCase) || heading.Contains(method.Name, StringComparison.OrdinalIgnoreCase))
                .Select(method => $"{file}#{method.Name}")
                .Take(5)
                .ToList();
            return Fail("NotFound", $"Heading '{heading}' was not found in '{file}'.", $"{file}#{heading}", suggestions);
        }

        if (candidates.Count > 1)
        {
            return Fail(
                "AmbiguousMatch",
                $"Heading prefix '{heading}' matched multiple sections in '{file}'.",
                $"{file}#{heading}",
                candidates.Select(method => $"{file}#{method.Name}"));
        }

        var selectedHeading = candidates[0].Name;
        var methods = (await _repositoryStore.FindMethodSourcesAsync(selectedHeading, cancellationToken))
            .Where(method => string.Equals(method.FilePath, snapshot.Path, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (methods.Count != 1)
        {
            return Fail("ParseError", $"Heading '{selectedHeading}' was indexed but its source could not be read.", $"{file}#{heading}");
        }

        var method = methods[0];
        var source = method.Source.Replace("\r\n", "\n");
        var returnedLines = source.Split('\n').Length;
        var response = new SourceReadResponse("section", method.Id, method.QualifiedName, method.FilePath, method.StartLine, method.EndLine, snapshot.ApproximateLoc, returnedLines, returnedLines < snapshot.ApproximateLoc, true, source, new { Heading = selectedHeading, RequestedHeading = heading });
        await _sessionStore.MarkSymbolExpandedAsync(new MarkSymbolExpandedRequest(session.Id, method.Id), cancellationToken);
        return new ProtocolResponse(true, JsonSerializer.SerializeToElement(response), [], []);
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

        return await LookupMethodSourcesAsync(query, cancellationToken);
    }

    private Task<IReadOnlyList<MethodSource>> LookupMethodSourcesAsync(string query, CancellationToken cancellationToken)
    {
        // Qualified Python module queries do not contain the synthetic "<module>" container used
        // by the index. Search broadly by the terminal method name, then apply strict path/container
        // resolution in ResolveSafeMethodMatches.
        var lookup = query.Contains('.') && !Guid.TryParse(query, out _)
            ? query[(query.LastIndexOf('.') + 1)..]
            : query;
        return _repositoryStore.FindMethodSourcesAsync(lookup, cancellationToken);
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

        var (result, error) = _patchLifecycleService.Apply(root, patch);
        if (error is not null)
        {
            return Fail(error.Code, error.Message);
        }

        return new ProtocolResponse(true, JsonSerializer.SerializeToElement(result), result!.ValidationErrors, []);
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

        var response = _patchLifecycleService.Preview(root, patch);
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

        var response = _patchLifecycleService.ValidateDocument(root, patch);
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

        var (response, error) = _patchLifecycleService.Rollback(root, patch);
        if (error is not null)
        {
            return Fail(error.Code, error.Message);
        }

        return new ProtocolResponse(true, JsonSerializer.SerializeToElement(response), [], []);
    }

    private static ProtocolResponse Fail(string code, string message, string? attemptedName = null, IEnumerable<string>? suggestions = null)
        => new(false, null, [], [new ProtocolError(code, message, attemptedName, suggestions?.Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList())]);

    private async Task<ProtocolResponse> NotFoundWithSuggestionsAsync(string kind, string query, CancellationToken cancellationToken)
    {
        var suggestions = _repositoryStore is ISymbolStore symbols
            ? (await symbols.FindClosestSymbolsAsync(query, cancellationToken: cancellationToken)).Select(symbol => symbol.QualifiedName).Where(name => !string.Equals(name, query, StringComparison.OrdinalIgnoreCase)).ToList()
            : [];
        var suffix = suggestions.Count == 0 ? string.Empty : $" Did you mean: {string.Join(", ", suggestions)}?";
        return Fail("NotFound", $"{kind} '{query}' was not found.{suffix}", query, suggestions);
    }

    private static IReadOnlyList<T> PreferExact<T>(IReadOnlyList<T> candidates, string query, params Func<T, string>[] selectors)
    {
        var exact = candidates.Where(candidate => selectors.Any(selector => string.Equals(selector(candidate), query, StringComparison.OrdinalIgnoreCase))).ToList();
        return exact.Count > 0 ? exact : candidates;
    }

    private static IReadOnlyList<MethodSource> ResolveSafeMethodMatches(IReadOnlyList<MethodSource> candidates, string query)
    {
        var exactIdentity = candidates.Where(method =>
            string.Equals(method.Id, query, StringComparison.OrdinalIgnoreCase)
            || string.Equals(method.QualifiedName, query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exactIdentity.Count > 0)
        {
            return exactIdentity;
        }

        var separator = query.LastIndexOf('.');
        var requestedName = separator >= 0 ? query[(separator + 1)..] : query;
        var named = candidates.Where(method =>
            string.Equals(method.QualifiedName.Split('.').Last(), requestedName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (named.Count == 0)
        {
            // Substring/fuzzy hits are suggestions, never executable read targets.
            return [];
        }

        if (separator < 0)
        {
            return named;
        }

        var qualifier = query[..separator].Replace('\\', '/').Trim('/');
        var qualifierPath = qualifier.Replace('.', '/');
        var pathMatches = named.Where(method =>
        {
            var pathWithoutExtension = Path.ChangeExtension(method.FilePath.Replace('\\', '/'), null) ?? string.Empty;
            return pathWithoutExtension.Equals(qualifierPath, StringComparison.OrdinalIgnoreCase)
                || pathWithoutExtension.EndsWith('/' + qualifierPath, StringComparison.OrdinalIgnoreCase);
        }).ToList();
        if (pathMatches.Count > 0)
        {
            return pathMatches;
        }

        var containerMatches = named.Where(method =>
        {
            var container = method.QualifiedName[..Math.Max(0, method.QualifiedName.LastIndexOf('.'))];
            return container.Equals(qualifier, StringComparison.OrdinalIgnoreCase)
                || container.EndsWith('.' + qualifier, StringComparison.OrdinalIgnoreCase);
        }).ToList();
        return containerMatches.Count > 0 ? containerMatches : named;
    }

    private static SessionId ParseSessionId(string sessionId) => new(Guid.Parse(sessionId));

    private static bool TryGetQuery(JsonElement payload, out string query)
    {
        query = string.Empty;
        return payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("query", out var queryElement)
            && queryElement.ValueKind == JsonValueKind.String
            && (query = queryElement.GetString() ?? string.Empty).Length > 0;
    }

    private static bool TryGetString(JsonElement payload, string propertyName, out string value)
    {
        value = string.Empty;
        return payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty(propertyName, out var element)
            && element.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value = element.GetString() ?? string.Empty);
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement payload, string propertyName)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return element.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
            .Select(item => item.GetString()!)
            .ToList();
    }

    private static int? ReadPositiveInt(JsonElement payload, string propertyName)
        => payload.ValueKind == JsonValueKind.Object
           && payload.TryGetProperty(propertyName, out var element)
           && element.TryGetInt32(out var value)
           && value > 0
            ? value
            : null;

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

    private RuntimeContextResolution ResolveValidationContext(string repositoryRoot, JsonElement payload, ValidationMode mode)
    {
        var runtime = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("runtime", out var runtimeElement) && runtimeElement.ValueKind == JsonValueKind.String
            ? runtimeElement.GetString()
            : null;
        var target = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("target", out var targetElement) && targetElement.ValueKind == JsonValueKind.String
            ? targetElement.GetString()
            : null;

        return _runtimeContextResolver.Resolve(repositoryRoot, runtime, target, mode);
    }
}
