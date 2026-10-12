using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
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

    /// <summary>Whether a write re-indexes the files it touched (owner ruling, brief OSS12 item 2:
    /// every write path re-indexes by default, `--no-index` opts out). An agent that edits and does
    /// not re-index leaves the index describing the old text, so the next `devx read` of what it
    /// just wrote can return the previous version -- which is why the harness used to tell agents
    /// to run `devx index update` by hand, 43 calls per series.</summary>
    private readonly bool _refreshIndexAfterEdit;

    public ProtocolEngine(ISearchService searchService, ISessionStore sessionStore, IRepositoryStore repositoryStore)
        : this(searchService, sessionStore, repositoryStore, new DotnetBuildTestService())
    {
    }

    public ProtocolEngine(ISearchService searchService, ISessionStore sessionStore, IRepositoryStore repositoryStore, DotnetBuildTestService buildTestService)
        : this(searchService, sessionStore, repositoryStore, buildTestService, new PatchLifecycleService())
    {
    }

    public ProtocolEngine(ISearchService searchService, ISessionStore sessionStore, IRepositoryStore repositoryStore, DotnetBuildTestService buildTestService, PatchLifecycleService patchLifecycleService)
        : this(searchService, sessionStore, repositoryStore, buildTestService, patchLifecycleService, refreshIndexAfterEdit: true)
    {
    }

    public ProtocolEngine(ISearchService searchService, ISessionStore sessionStore, IRepositoryStore repositoryStore, DotnetBuildTestService buildTestService, PatchLifecycleService patchLifecycleService, bool refreshIndexAfterEdit)
    {
        _searchService = searchService;
        _sessionStore = sessionStore;
        _repositoryStore = repositoryStore;
        _buildTestService = buildTestService;
        _patchLifecycleService = patchLifecycleService;
        _refreshIndexAfterEdit = refreshIndexAfterEdit;
        _runtimeContextResolver = new RuntimeContextResolver();
    }

    private readonly AsyncLocal<System.Runtime.CompilerServices.StrongBox<string?>> _historyFailure = new();

    private async Task RecordHistoryAsync(Func<Task> save)
    {
        try { await save(); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (_historyFailure.Value is { Value: null } box) box.Value = error.Message;
        }
    }

    public async Task<ProtocolResponse> ExecuteAsync(ProtocolRequest request, CancellationToken cancellationToken = default)
    {
        _historyFailure.Value = new System.Runtime.CompilerServices.StrongBox<string?>();
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

        if (RequiresFreshIndex(request.Operation))
        {
            if (_repositoryStore is not IIndexFreshnessStore freshnessStore)
            {
                return Fail("IndexFreshnessUnavailable", "The repository store cannot confirm index freshness; indexed operations are blocked.");
            }

            var dirtyRepositories = await freshnessStore.GetDirtyIndexRepositoriesAsync(cancellationToken);
            if (dirtyRepositories.Count > 0)
            {
                var roots = string.Join(", ", dirtyRepositories.Select(repository => repository.RepositoryRoot));
                return Fail("IndexDirty", $"Indexed operations are blocked because source changes are not reconciled for: {roots}. Run 'devx index <root>' to reconcile the index.");
            }
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
                ProtocolOperations.RunVerify => await HandleRunVerifyAsync(request, session, cancellationToken),
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
                    await RecordHistoryAsync(() => _sessionStore.AddOperationMetricAsync(new OperationMetric(request.Operation.ToLowerInvariant().Replace('_', ' '),
                        readStarted, DateTimeOffset.UtcNow - readStarted, true, session.Id.Value.ToString(), metricMetadata.ToJsonString(), request.AgentLabel), cancellationToken));
                }
            }
            if (response.Success && _historyFailure.Value?.Value is { } historyFailure) response = response with { Diagnostics = [.. response.Diagnostics, new ProtocolDiagnostic("HistoryNotRecorded", "history not recorded: " + historyFailure)] };
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
        await RecordHistoryAsync(() => _sessionStore.SaveSearchAsync(new SaveSearchRequest(session.Id, query), cancellationToken));
        return Success(result);
    }

    private async Task<ProtocolResponse> HandleSearchTextAsync(ProtocolRequest request, SessionState session, CancellationToken cancellationToken)
    {
        if (!TryGetQuery(request.Payload, out var query))
        {
            return Fail("ValidationError", "Search requests must include a string 'query' field.");
        }

        var result = await _searchService.SearchTextAsync(new SearchTextRequest(query), cancellationToken);
        await RecordHistoryAsync(() => _sessionStore.SaveSearchAsync(new SaveSearchRequest(session.Id, query), cancellationToken));
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

        var agentFormat = request.Payload.ValueKind == JsonValueKind.Object
            && request.Payload.TryGetProperty("format", out var outputFormat)
            && outputFormat.ValueKind == JsonValueKind.String
            && outputFormat.GetString() == "agent";
        if (!agentFormat)
        {
            snapshot = snapshot with
            {
                Types = snapshot.Types.Select(type => type with { StartLine = null, EndLine = null }).ToArray(),
                Methods = snapshot.Methods.Select(method => method with { Signature = null, StartLine = null, EndLine = null }).ToArray()
            };
        }

        await RecordHistoryAsync(() => _sessionStore.AddWorkingSetItemAsync(new AddWorkingSetItemRequest(session.Id, fileIdOrPath), cancellationToken));
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
        await RecordHistoryAsync(() => _sessionStore.AddWorkingSetItemAsync(new AddWorkingSetItemRequest(session.Id, workingSetItem), cancellationToken));
        if (selected is not null)
        {
            await RecordHistoryAsync(() => _sessionStore.MarkSymbolExpandedAsync(new MarkSymbolExpandedRequest(session.Id, selected.Id), cancellationToken));
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
        await RecordHistoryAsync(() => _sessionStore.AddWorkingSetItemAsync(new AddWorkingSetItemRequest(session.Id, method.Id), cancellationToken));
        await RecordHistoryAsync(() => _sessionStore.MarkSymbolExpandedAsync(new MarkSymbolExpandedRequest(session.Id, method.Id), cancellationToken));
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
        await RecordHistoryAsync(() => _sessionStore.AddWorkingSetItemAsync(new AddWorkingSetItemRequest(session.Id, type.Id), cancellationToken));
        await RecordHistoryAsync(() => _sessionStore.MarkSymbolExpandedAsync(new MarkSymbolExpandedRequest(session.Id, type.Id), cancellationToken));
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
            await RecordHistoryAsync(() => _sessionStore.AddWorkingSetItemAsync(new AddWorkingSetItemRequest(session.Id, caller.CallerMethodId), cancellationToken));
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
            await RecordHistoryAsync(() => _sessionStore.AddWorkingSetItemAsync(new AddWorkingSetItemRequest(session.Id, callee.CalleeMethodId), cancellationToken));
        }

        return new ProtocolResponse(true, JsonSerializer.SerializeToElement(new CalleesResponse(new CalleeSummaryResponse(query, target is null ? "Unknown" : "Exact", target, callees))), [], []);
    }

    private static ProtocolResponse? UnsupportedPythonGraphResponse(string query, IReadOnlyList<MethodSummary> methods)
        => methods.Any(method => method.FilePath.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
            ? Fail("UnsupportedLanguage", $"Call graph queries are not supported for Python symbol '{query}'.")
            : null;

    /// <summary>
    /// A bare "Build failed." / "Tests failed." is what sent the LG1 agent to grep: the run had
    /// already parsed the file, the line and the message, and then threw them away in the error.
    /// The first few are rendered in the same `path:line: message` form the CLI summary uses,
    /// and the full list stays in the log path the execution record carries.
    /// </summary>
    private static string CompactFailure(string verb, ExecutionRecord execution, string root)
    {
        var lines = execution.Diagnostics
            .Where(diagnostic => !string.IsNullOrWhiteSpace(diagnostic.Message))
            .Take(5)
            .Select(diagnostic => CompactDiagnostic(diagnostic, root))
            .ToList();
        if (lines.Count == 0)
        {
            return string.IsNullOrWhiteSpace(execution.Summary) ? $"{verb} failed." : execution.Summary;
        }
        return $"{verb} failed:" + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }

    private static string CompactDiagnostic(DiagnosticRecord diagnostic, string root)
    {
        var file = diagnostic.File;
        if (!string.IsNullOrWhiteSpace(file) && Path.IsPathRooted(file)) file = Path.GetRelativePath(root, file);
        var message = diagnostic.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return string.IsNullOrWhiteSpace(file)
            ? $"{diagnostic.Code}: {message}"
            : $"{file}:{diagnostic.Line ?? 0}: {diagnostic.Code} {message}";
    }

    private async Task<ProtocolResponse> HandleRunBuildAsync(ProtocolRequest request, SessionState session, CancellationToken cancellationToken)
    {
        var root = await _repositoryStore.GetRepositoryRootPathAsync(session.RepositoryId, cancellationToken);
        if (string.IsNullOrWhiteSpace(root))
        {
            return Fail("RepositoryNotIndexed", "Repository root was not found.");
        }

        var resolution = ResolveValidationContext(root, request.Payload, ValidationMode.Build);
        var configured = DevXCommandConfig.TryRead(root, "build") is not null;
        if ((!resolution.Success || resolution.Context is null || resolution.SelectedTarget is null) && !configured)
        {
            return Fail("ValidationError", resolution.Errors.FirstOrDefault() ?? "Runtime target resolution failed.");
        }

        var (buildContext, buildTarget) = ResolvedOrConfigured(root, resolution, "build");
        var execution = await _buildTestService.RunBuildAsync(root, resolution.TargetSelector, cancellationToken);
        await _sessionStore.AddExecutionAsync(new AddExecutionRequest(session.Id, execution), cancellationToken);
        foreach (var diagnostic in execution.Diagnostics)
        {
            await _sessionStore.AddDiagnosticAsync(new AddDiagnosticRequest(session.Id, diagnostic), cancellationToken);
        }

        var response = new ValidationExecutionResponse("build", buildContext, buildTarget, execution);
        return new ProtocolResponse(execution.Success, JsonSerializer.SerializeToElement(response), [], execution.Success ? [] : [new ProtocolError("ValidationError", CompactFailure("Build", execution, root))]);
    }

    private async Task<ProtocolResponse> HandleRunTestsAsync(ProtocolRequest request, SessionState session, CancellationToken cancellationToken)
    {
        var root = await _repositoryStore.GetRepositoryRootPathAsync(session.RepositoryId, cancellationToken);
        if (string.IsNullOrWhiteSpace(root))
        {
            return Fail("RepositoryNotIndexed", "Repository root was not found.");
        }

        var resolution = ResolveValidationContext(root, request.Payload, ValidationMode.Test);
        var configured = DevXCommandConfig.TryRead(root, "test") is not null;
        if ((!resolution.Success || resolution.Context is null || resolution.SelectedTarget is null) && !configured)
        {
            return Fail("ValidationError", resolution.Errors.FirstOrDefault() ?? "Runtime target resolution failed.");
        }

        var targets = ReadStringArray(request.Payload, "targets");
        // A target the caller did not name is filled in from runtime discovery -- but only when
        // the repository has not answered for itself. A configured test command takes `{target}`
        // as the caller wrote it, and a bare `devx test` must leave it empty: substituting the
        // discovered project path would run `runtests.py .` instead of `runtests.py`.
        if (targets.Count == 0 && !configured && !string.IsNullOrWhiteSpace(resolution.TargetSelector))
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

        var (testContext, testTarget) = ResolvedOrConfigured(root, resolution, "test");
        var response = new ValidationExecutionResponse("test", testContext, testTarget, execution);
        return new ProtocolResponse(execution.Success, JsonSerializer.SerializeToElement(response), [], execution.Success ? [] : [new ProtocolError("ValidationError", CompactFailure("Tests", execution, root))]);
    }

    /// <summary>`devx verify [build|test|both] [target...]` in one call: the build, then the
    /// relevant tests unless the build failed or the mode was build-only. Both runs come back in
    /// one response, so an agent that edits and checks spends one call instead of three.
    /// A build failure skips the tests and is reported, never hidden (brief OSS14 item 1).</summary>
    private async Task<ProtocolResponse> HandleRunVerifyAsync(ProtocolRequest request, SessionState session, CancellationToken cancellationToken)
    {
        var root = await _repositoryStore.GetRepositoryRootPathAsync(session.RepositoryId, cancellationToken);
        if (string.IsNullOrWhiteSpace(root))
        {
            return Fail("RepositoryNotIndexed", "Repository root was not found.");
        }

        var mode = VerifyAfterEdit.ParseMode(ReadString(request.Payload, "mode"));
        var configuredBuild = DevXCommandConfig.TryRead(root, "build") is not null;
        var configuredTests = DevXCommandConfig.TryRead(root, "test") is not null;

        var buildResolution = ResolveValidationContext(root, request.Payload, ValidationMode.Build);
        if ((!buildResolution.Success || buildResolution.Context is null || buildResolution.SelectedTarget is null) && !configuredBuild)
        {
            return Fail("ValidationError", buildResolution.Errors.FirstOrDefault() ?? "Runtime target resolution failed.");
        }

        var (buildContext, buildTarget) = ResolvedOrConfigured(root, buildResolution, "build");
        // `--verify=test` is the Python case: there is nothing to compile, so a build record that
        // says "skipped" is more honest than a py_compile pass dressed up as a build.
        var buildExecution = mode == VerifyMode.Test
            ? VerifyAfterEdit.TestOnlyBuild(ReadString(request.Payload, "target") ?? string.Empty)
            : await _buildTestService.RunBuildAsync(root, buildResolution.TargetSelector, cancellationToken);
        await _sessionStore.AddExecutionAsync(new AddExecutionRequest(session.Id, buildExecution), cancellationToken);
        foreach (var diagnostic in buildExecution.Diagnostics)
        {
            await _sessionStore.AddDiagnosticAsync(new AddDiagnosticRequest(session.Id, diagnostic), cancellationToken);
        }
        var buildResponse = new ValidationExecutionResponse("build", buildContext, buildTarget, buildExecution);

        if (!buildExecution.Success || mode == VerifyMode.Build)
        {
            var testsSkipped = !buildExecution.Success;
            var only = new VerifyExecutionResponse(VerifyAfterEdit.ModeName(mode), buildResponse, null, testsSkipped);
            return new ProtocolResponse(buildExecution.Success, JsonSerializer.SerializeToElement(only), [],
                buildExecution.Success ? [] : [new ProtocolError("ValidationError", CompactFailure("Build", buildExecution, root))]);
        }

        var testResolution = ResolveValidationContext(root, request.Payload, ValidationMode.Test);
        if ((!testResolution.Success || testResolution.Context is null || testResolution.SelectedTarget is null) && !configuredTests)
        {
            var unresolved = new VerifyExecutionResponse(VerifyAfterEdit.ModeName(mode), buildResponse, null, true);
            return new ProtocolResponse(false, JsonSerializer.SerializeToElement(unresolved), [],
                [new ProtocolError("ValidationError", testResolution.Errors.FirstOrDefault() ?? "Runtime target resolution failed.")]);
        }

        var targets = ReadStringArray(request.Payload, "targets");
        if (targets.Count == 0 && !configuredTests && !string.IsNullOrWhiteSpace(testResolution.TargetSelector))
        {
            targets = [testResolution.TargetSelector];
        }
        else if (targets.Count == 1 && VerifyAfterEdit.HasBuildStep(targets[0]) && !VerifyAfterEdit.IsTestFile(root, Path.Combine(root, targets[0])))
        {
            // A named source file means "the tests for this file", not "run this file as a test" --
            // the same rule `devx replace --verify` uses, so the two never disagree.
            var relevant = VerifyAfterEdit.FindRelevantTestFiles(root, targets[0]);
            if (relevant.Count > 0) targets = relevant;
        }

        var timeoutSeconds = ReadPositiveInt(request.Payload, "timeoutSeconds") ?? 300;
        var testFilter = ReadString(request.Payload, "filter");
        var testExecution = await _buildTestService.RunTestsAsync(root, targets, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken, testFilter);
        await _sessionStore.AddExecutionAsync(new AddExecutionRequest(session.Id, testExecution), cancellationToken);
        foreach (var diagnostic in testExecution.Diagnostics)
        {
            await _sessionStore.AddDiagnosticAsync(new AddDiagnosticRequest(session.Id, diagnostic), cancellationToken);
        }

        var (testContext, testTarget) = ResolvedOrConfigured(root, testResolution, "test");
        var testResponse = new ValidationExecutionResponse("test", testContext, testTarget, testExecution);
        var success = testExecution.Success;
        var result = new VerifyExecutionResponse(VerifyAfterEdit.ModeName(mode), buildResponse, testResponse, false);
        return new ProtocolResponse(success, JsonSerializer.SerializeToElement(result), [],
            success ? [] : [new ProtocolError("ValidationError", CompactFailure("Tests", testExecution, root))]);
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
            return Fail("ValidationError", "Read file requests must include either a 'range' field formatted as 'start:end' or 'start-end', or --full.");
        }

        var root = await _repositoryStore.GetRepositoryRootPathAsync(session.RepositoryId, cancellationToken);
        if (string.IsNullOrWhiteSpace(root))
        {
            return Fail("RepositoryNotIndexed", "Repository root was not found.");
        }

        if (!RepositoryPathGuard.TryResolve(root, path, out var absolutePath, out var pathError))
        {
            return Fail("ValidationError", pathError);
        }

        if (!IOFile.Exists(absolutePath)) return Fail("FileMissing", $"file missing: {path}", path);
        var source = await IOFile.ReadAllTextAsync(absolutePath, cancellationToken);
        var lines = source.Split('\n');
        var safeStart = full ? 1 : Math.Max(1, startLine);
        var requestedEnd = full ? lines.Length : Math.Min(endLine, lines.Length);
        // A range is capped and paged, never silently widened: an agent that asks for 1-99999
        // gets a slice plus the range that continues it, rather than a whole file in its context.
        var safeEnd = full ? requestedEnd : Math.Min(requestedEnd, safeStart + MaxRangeLines - 1);
        if (safeStart > safeEnd)
        {
            return Fail("ValidationError", "Invalid line range.");
        }

        var slice = string.Join('\n', lines[(safeStart - 1)..safeEnd]);
        var nextRange = safeEnd < requestedEnd ? $"{safeEnd + 1}:{requestedEnd}" : null;
        var response = new SourceReadResponse(
            Kind: "file",
            SymbolId: path,
            QualifiedName: path,
            Path: path,
            StartLine: safeStart,
            EndLine: safeEnd,
            TotalLines: lines.Length,
            ReturnedLines: safeEnd - safeStart + 1,
            Truncated: safeEnd < lines.Length,
            SourceReturned: true,
            Source: slice,
            Metadata: new { Language = Path.GetExtension(path).TrimStart('.').ToUpperInvariant(), NextRange = nextRange });
        return new ProtocolResponse(true, JsonSerializer.SerializeToElement(response), [], []);
    }

    private const int MaxRangeLines = 400;

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
                    var candidates = RankCandidates(resolution.Candidates, query);
                    var total = resolution.Candidates.Count;
                    return new ProtocolResponse(false, null, [], [new ProtocolError("AmbiguousMatch",
                        $"Symbol '{query}' matched multiple definitions ({total}); pass a qualified name.",
                        query, candidates.Select(s => s.QualifiedName).ToList(), candidates, total)]);
                }
                if (resolution.Candidates.Count == 1)
                {
                    var symbol = resolution.Candidates[0];
                    if (!RepositoryPathGuard.TryResolve(root, symbol.FilePath!, out var absolute, out var pathError))
                        return Fail("ValidationError", pathError, query);
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
                        object symbolMetadata;
                        if ((kind is "method" or "function") && WantsReadContext(request.Payload))
                        {
                            var indexedSymbol = _repositoryStore is ISymbolStore symbolStore
                                ? (await symbolStore.FindSymbolsByQualifiedNameAsync(symbol.QualifiedName, cancellationToken)).FirstOrDefault(candidate => candidate.Id == symbol.Id)
                                : null;
                            var context = indexedSymbol is null
                                ? new { Complete = false, Basis = "bounded indexed path/name candidates", TestNameMatches = Array.Empty<object>(), SameFileCandidates = Array.Empty<object>(), TestNameMatchesTruncated = false, SameFileCandidatesTruncated = false, SourceNamesTruncated = false, Note = "The resolved symbol has no indexed symbol record for context lookup." }
                                : await BuildReadContextAsync(indexedSymbol, source, cancellationToken);
                            symbolMetadata = new { ResolvedVia = resolution.ResolvedVia, SymbolKind = symbol.Kind, Message = message, Context = context };
                        }
                        else
                        {
                            symbolMetadata = new { ResolvedVia = resolution.ResolvedVia, SymbolKind = kind == "section" ? "Section" : symbol.Kind, Message = message };
                        }
                        var result = new SourceReadResponse(kind, symbolId, symbol.QualifiedName, symbol.FilePath!, symbol.StartLine.Value,
                            symbol.EndLine.Value, symbolTotalLines, symbolReturnedLines, symbolReturnedLines < symbol.EndLine.Value - symbol.StartLine.Value + 1, true, source, symbolMetadata);
                        await RecordHistoryAsync(() => _sessionStore.MarkSymbolExpandedAsync(new MarkSymbolExpandedRequest(session.Id, symbolId), cancellationToken));
                        return new ProtocolResponse(true, JsonSerializer.SerializeToElement(result), [], []);
                    }
                }
                // Legacy source IDs use a different identity from indexed symbols; retain ID support.
                if (resolution.Candidates.Count == 0 && !Guid.TryParse(query, out _))
                {
                    // A dotted miss still has to hand back names the caller can paste. LG1 was
                    // told only "not in this workspace" and left the tool for grep; the index
                    // can always answer for the last segment, which is one prefix shorter.
                    // The workspace fact is still worth saying -- it is how a caller knows the
                    // name belongs to a different checkout rather than being misspelled -- so
                    // it is kept and the pasteable names are added to it.
                    return await NotFoundWithSuggestionsAsync(
                        "Symbol",
                        query,
                        cancellationToken,
                        $"Symbol '{query}' is not in this workspace: {root}.");
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
        var methodRoot = await _repositoryStore.GetRepositoryRootPathAsync(session.RepositoryId, cancellationToken);
        if (string.IsNullOrWhiteSpace(methodRoot))
        {
            return Fail("RepositoryNotIndexed", "Repository root was not found.");
        }
        if (!RepositoryPathGuard.TryResolve(methodRoot, method.FilePath, out _, out var methodPathError))
        {
            return Fail("ValidationError", methodPathError, query);
        }
        var slice = method.Source.Replace("\r\n", "\n");
        var returnedLines = slice.Split('\n').Length;
        var snapshot = await _repositoryStore.GetFileSnapshotAsync(method.FilePath, cancellationToken);
        var totalLines = Math.Max(snapshot?.ApproximateLoc ?? method.EndLine, returnedLines);
        object metadata;
        if (WantsReadContext(request.Payload))
        {
            var indexedSymbol = _repositoryStore is IReadContextStore contextStore
                ? await contextStore.FindReadContextTargetAsync(session.RepositoryId, method.FilePath, method.QualifiedName, cancellationToken)
                : null;
            var context = indexedSymbol is null
                ? new { Complete = false, Basis = "bounded indexed path/name candidates", TestNameMatches = Array.Empty<object>(), SameFileCandidates = Array.Empty<object>(), TestNameMatchesTruncated = false, SameFileCandidatesTruncated = false, SourceNamesTruncated = false, Note = _repositoryStore is IReadContextStore ? "The resolved method has no indexed symbol record for context lookup." : "This store does not provide read context candidates." }
                : await BuildReadContextAsync(indexedSymbol, slice, cancellationToken);
            metadata = new { ResolvedVia = Guid.TryParse(query, out _) ? "exact" : "short-name", Context = context };
        }
        else
        {
            metadata = new { ResolvedVia = Guid.TryParse(query, out _) ? "exact" : "short-name" };
        }
        var response = new SourceReadResponse("method", method.Id, method.QualifiedName, method.FilePath, method.StartLine, method.EndLine, totalLines, returnedLines, returnedLines < method.EndLine - method.StartLine + 1, true, slice, metadata);
        await RecordHistoryAsync(() => _sessionStore.MarkSymbolExpandedAsync(new MarkSymbolExpandedRequest(session.Id, method.Id), cancellationToken));
        return new ProtocolResponse(true, JsonSerializer.SerializeToElement(response), [], []);
    }

    private static bool WantsReadContext(JsonElement payload)
        => payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("context", out var context)
            && context.ValueKind == JsonValueKind.True;

    private async Task<object> BuildReadContextAsync(Symbol target, string source, CancellationToken cancellationToken)
    {
        const int sourceScanLimit = 32 * 1024;
        var sourceScan = source.Length > sourceScanLimit ? source[..sourceScanLimit] : source;
        var sourceNamesTruncated = source.Length > sourceScanLimit;
        var sourceNames = Regex.Matches(sourceScan, @"([A-Za-z_]\w*)\s*\(")
            .Select(match => match.Groups[1].Value)
            .Where(name => name is not ("if" or "for" or "while" or "switch" or "catch" or "def" or "with" or "return" or "assert" or "lambda"))
            .Distinct(StringComparer.Ordinal)
            .Take(65)
            .ToList();
        if (_repositoryStore is not IReadContextStore contextStore)
        {
            return new
            {
                Complete = false,
                Basis = "bounded indexed path/name candidates",
                TestNameMatches = Array.Empty<object>(),
                SameFileCandidates = Array.Empty<object>(),
                TestNameMatchesTruncated = false,
                SameFileCandidatesTruncated = false,
                SourceNamesTruncated = sourceNamesTruncated || sourceNames.Count > 64,
                Note = "This store does not provide read context candidates."
            };
        }

        var candidates = await contextStore.FindReadContextCandidatesAsync(target, sourceNames, sourceNamesTruncated, 5, cancellationToken);
        return new
        {
            Complete = false,
            Basis = "bounded indexed path/name candidates",
            TestNameMatches = candidates.TestNameMatches.Select(symbol => ContextCandidate(symbol, "test-name-match", "exact test name and same-module test path")).ToList(),
            SameFileCandidates = candidates.SameFileDefinitions.Select(symbol => ContextCandidate(
                symbol,
                candidates.SameFileDefinitionsAreNameMatches ? "source-name-match" : "same-file-definition",
                candidates.SameFileDefinitionsAreNameMatches ? "lexical source name and indexed definition" : "indexed definition in the same file")).ToList(),
            TestNameMatchesTruncated = candidates.TestNameMatchesTruncated,
            SameFileCandidatesTruncated = candidates.SameFileDefinitionsTruncated,
            SourceNamesTruncated = candidates.SourceNamesTruncated,
            Note = "Candidates are navigation hints; name and path matches do not establish call or test relations."
        };
    }

    private static object ContextCandidate(Symbol symbol, string relation, string evidence)
        => new
        {
            SymbolId = symbol.Id,
            symbol.Name,
            symbol.QualifiedName,
            symbol.Kind,
            symbol.FilePath,
            symbol.StartLine,
            symbol.EndLine,
            Relation = relation,
            Evidence = evidence
        };

    // Member lines omit the path (always the header's file) to keep the outline small.
    // Stored signatures may span lines (C# keeps the body braces); the outline shows the first line only.
    private static string OutlineSignature(string? signature)
    {
        var first = (signature ?? "").Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0) ?? "";
        if (first.Length > 160) first = first[..160];
        return first.Length == 0 ? "" : " " + first;
    }

    private async Task<(string Text, int Count)> BuildTypeOutlineAsync(string qualifiedName, string filePath, int startLine, int endLine, string[] fileLines, CancellationToken cancellationToken)
    {
        // Signature: the declaration line, extended while parentheses are open (multi-line Python bases)
        // or until a line ends with ':' (Python). C#-style declarations stop after the first line.
        var signature = new List<string>();
        var depth = 0;
        for (var i = startLine - 1; i < endLine && i < fileLines.Length; i++)
        {
            var line = fileLines[i];
            signature.Add(line);
            depth += line.Count(c => c is '(' or '[') - line.Count(c => c is ')' or ']');
            if (depth <= 0) break;
        }
        var members = _repositoryStore is ISymbolStore symbols
            ? (await symbols.FindSymbolsContainingAsync(qualifiedName, cancellationToken))
                .Where(symbol => symbol.FilePath == filePath && symbol.StartLine > startLine && symbol.EndLine <= endLine && symbol.QualifiedName.StartsWith(qualifiedName + ".", StringComparison.Ordinal))
                .OrderBy(symbol => symbol.StartLine).ToList()
            : [];
        var lines = new List<string>(signature);
        foreach (var member in members)
        {
            var memberName = member.QualifiedName[(qualifiedName.Length + 1)..];
            if (memberName.Contains('.')) continue;
            lines.Add($"{member.Kind.ToLowerInvariant()} {memberName} {member.StartLine}-{member.EndLine}" + OutlineSignature(member.Signature));
        }
        return (string.Join('\n', lines), members.Count(member => !member.QualifiedName[(qualifiedName.Length + 1)..].Contains('.')));
    }

    private async Task<ProtocolResponse> HandleReadTypeAsync(ProtocolRequest request, SessionState session, CancellationToken cancellationToken)
    {
        var query = GetReadQuery(request.Payload);
        if (query is null)
        {
            return Fail("ValidationError", "Read type requests must include either a string 'query' field or an 'id' field.");
        }

        ReadableResolution? typeResolution = null;
        if (_repositoryStore is ISymbolStore)
        {
            var typeRoot = await _repositoryStore.GetRepositoryRootPathAsync(session.RepositoryId, cancellationToken);
            if (typeRoot is not null)
            {
                var indexed = await ReadableSymbolResolver.ResolveAsync(_repositoryStore, typeRoot, query, cancellationToken);
                indexed = indexed with { Candidates = indexed.Candidates.Where(candidate => candidate.Kind is "Class" or "Type" or "Record" or "Interface" or "Struct" or "Enum").ToList() };
                typeResolution = indexed;
                if (indexed.Candidates.Count > 1)
                {
                    var ranked = RankCandidates(indexed.Candidates, query);
                    var total = indexed.Candidates.Count;
                    return new ProtocolResponse(false, null, [], [new ProtocolError("AmbiguousMatch", $"Type '{query}' matched multiple definitions ({total}); pass a qualified name.", query, ranked.Select(item => item.QualifiedName).ToList(), ranked, total)]);
                }
                if (indexed.Candidates.Count == 1 && indexed.Candidates[0].FileStatus == "unsafe path")
                    return Fail("ValidationError", $"Indexed path for '{query}' contains a symbolic link or escapes the repository root.", query);
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

            var idRoot = await _repositoryStore.GetRepositoryRootPathAsync(session.RepositoryId, cancellationToken);
            if (idRoot is null) return Fail("RepositoryNotIndexed", "Repository root was not found.");
            if (!RepositoryPathGuard.TryResolve(idRoot, typeSummary.FilePath, out var idAbsolute, out var idPathError)) return Fail("ValidationError", idPathError, id);
            if (!IOFile.Exists(idAbsolute)) return Fail("FileMissing", $"file missing: {typeSummary.FilePath}", id);
            var idLines = (await IOFile.ReadAllTextAsync(idAbsolute, cancellationToken)).Replace("\r\n", "\n").Split('\n');
            var idEndLine = typeSummary.EndLine;
            var idSource = string.Join('\n', idLines[(typeSummary.StartLine - 1)..idEndLine]);
            var idReturned = idSource.Split('\n').Length;
            var (idOutline, idCount) = TryGetBoolean(request.Payload, "full") ? (idSource, 0) : await BuildTypeOutlineAsync(typeSummary.QualifiedName, typeSummary.FilePath, typeSummary.StartLine, typeSummary.EndLine, idLines, cancellationToken);
            var idMetadata = TryGetBoolean(request.Payload, "full") ? (object)new { TypeName = typeSummary.Name, Namespace = typeSummary.Namespace, Project = typeSummary.Project, Kind = typeSummary.Kind, ResolvedVia = "exact" } : new { TypeName = typeSummary.Name, Namespace = typeSummary.Namespace, Project = typeSummary.Project, Kind = typeSummary.Kind, ResolvedVia = "exact", Outline = true, MemberCount = idCount, BodyLines = typeSummary.EndLine - typeSummary.StartLine + 1, Hint = "pass --full for the body" };
            var responseById = new SourceReadResponse("type", typeSummary.Id, typeSummary.QualifiedName, typeSummary.FilePath, typeSummary.StartLine, typeSummary.EndLine, typeSummary.EndLine - typeSummary.StartLine + 1, idOutline.Split('\n').Length, false, true, idOutline, idMetadata);
            await RecordHistoryAsync(() => _sessionStore.MarkSymbolExpandedAsync(new MarkSymbolExpandedRequest(session.Id, typeSummary.Id), cancellationToken));
            return new ProtocolResponse(true, JsonSerializer.SerializeToElement(responseById), [], []);
        }

        var types = await ResolveTypeSummariesAsync(query, request.Payload, cancellationToken);
        types = PreferExact(types, query, type => type.Id, type => type.QualifiedName, type => type.Name);
        if (types.Count == 0 && typeResolution?.Candidates.Count == 1)
        {
            var found = await _repositoryStore.FindTypeSummaryByIdAsync(typeResolution.Candidates[0].Id, cancellationToken);
            if (found is not null) types = [found];
        }
        if (types.Count == 0)
        {
            return await NotFoundWithSuggestionsAsync("Type", query, cancellationToken);
        }

        if (types.Count > 1)
        {
            var ranked = RankCandidates(types.Select(type => new SymbolSearchResult(type.Id, type.Kind, type.Name, type.QualifiedName, type.FilePath, type.StartLine, type.EndLine)).ToList(), query);
            var total = types.Count;
            return new ProtocolResponse(false, null, [], [new ProtocolError("AmbiguousMatch", $"Type '{query}' matched multiple definitions ({total}); pass a qualified name.", query, ranked.Select(item => item.QualifiedName).ToList(), ranked, total)]);
        }

        var type = types[0];
        var root = await _repositoryStore.GetRepositoryRootPathAsync(session.RepositoryId, cancellationToken);
        if (root is null) return Fail("RepositoryNotIndexed", "Repository root was not found.");
        if (!RepositoryPathGuard.TryResolve(root, type.FilePath, out var absolute, out var pathError)) return Fail("ValidationError", pathError, query);
        if (!IOFile.Exists(absolute)) return Fail("FileMissing", $"file missing: {type.FilePath}", query);
        var fileLines = (await IOFile.ReadAllTextAsync(absolute, cancellationToken)).Replace("\r\n", "\n").Split('\n');
        if (type.StartLine < 1 || type.EndLine > fileLines.Length) return Fail("StaleIndex", $"Indexed span is stale for {type.FilePath}; run devx index update.", query);
        var typeFull = TryGetBoolean(request.Payload, "full");
        var typeEndLine = type.EndLine;
        var bodySource = string.Join('\n', fileLines[(type.StartLine - 1)..typeEndLine]);
        var (classSource, memberCount) = typeFull ? (bodySource, 0) : await BuildTypeOutlineAsync(type.QualifiedName, type.FilePath, type.StartLine, type.EndLine, fileLines, cancellationToken);
        var classLines = classSource.Split('\n').Length;
        var typeVia = typeResolution?.ResolvedVia ?? "exact";
        var requestedTypeLines = type.EndLine - type.StartLine + 1;
        var metadata = typeFull ? (object)new { TypeName = type.Name, Namespace = type.Namespace, Project = type.Project, Kind = type.Kind, ResolvedVia = typeVia } : new { TypeName = type.Name, Namespace = type.Namespace, Project = type.Project, Kind = type.Kind, ResolvedVia = typeVia, Outline = true, MemberCount = memberCount, BodyLines = requestedTypeLines, Hint = "pass --full for the body" };
        var response = new SourceReadResponse("type", type.Id, type.QualifiedName, type.FilePath, type.StartLine, type.EndLine, requestedTypeLines, classLines, false, true, classSource, metadata);
        await RecordHistoryAsync(() => _sessionStore.MarkSymbolExpandedAsync(new MarkSymbolExpandedRequest(session.Id, type.Id), cancellationToken));
        return new ProtocolResponse(true, JsonSerializer.SerializeToElement(response), [], []);
    }

    private async Task<ProtocolResponse> HandleReadSectionAsync(ProtocolRequest request, SessionState session, CancellationToken cancellationToken)
    {
        if (!TryGetString(request.Payload, "file", out var file))
        {
            return Fail("ValidationError", "Read section requests must use 'file#heading'.");
        }

        // The name is optional. `read section <file>#` asks for the file, and on a source file
        // `read section <file>#<name>` asks for a symbol -- both are how a caller actually
        // typed it, and both used to be answered with an error.
        var heading = TryGetString(request.Payload, "heading", out var providedHeading) ? providedHeading : string.Empty;
        if (string.IsNullOrWhiteSpace(heading))
        {
            return await ReadSectionTargetAsync(request, session, file, string.Empty, cancellationToken);
        }

        if (!Path.GetExtension(file).Equals(".md", StringComparison.OrdinalIgnoreCase))
        {
            return await ReadSectionTargetAsync(request, session, file, heading, cancellationToken);
        }

        var sectionRoot = await _repositoryStore.GetRepositoryRootPathAsync(session.RepositoryId, cancellationToken);
        if (string.IsNullOrWhiteSpace(sectionRoot))
        {
            return Fail("RepositoryNotIndexed", "Repository root was not found.");
        }
        if (!RepositoryPathGuard.TryResolve(sectionRoot, file, out var sectionPath, out var sectionPathError))
            return Fail("ValidationError", sectionPathError, $"{file}#{heading}");
        if (!IOFile.Exists(sectionPath)) return Fail("FileMissing", $"file missing: {file}", $"{file}#{heading}");

        if (_repositoryStore is not IRootScopedFileSnapshotStore rootScopedSnapshotStore)
        {
            return Fail("UnsupportedStoreCapability", "The repository store cannot scope a file snapshot to this repository.", file);
        }
        var relativeSectionPath = Path.GetRelativePath(Path.GetFullPath(sectionRoot), sectionPath).Replace('\\', '/');
        var snapshot = await rootScopedSnapshotStore.GetFileSnapshotAsync(session.RepositoryId, sectionRoot, relativeSectionPath, cancellationToken);
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
        // Resolve through the repository-scoped symbol path. The legacy name+path method
        // lookup is global and can select another repository's same-relative-path source.
        return await ReadSectionTargetAsync(request, session, file, selectedHeading, cancellationToken);
    }

    // The forgiving half of `read section`. A source file is not a document of headings, so
    // `<file>#<name>` resolves <name> as a symbol in that file and answers with the same
    // response `read method` gives, saying which symbol it resolved. `<file>#` with no name
    // answers with the file, capped and paged exactly as `read file` does.
    private async Task<ProtocolResponse> ReadSectionTargetAsync(ProtocolRequest request, SessionState session, string file, string name, CancellationToken cancellationToken)
    {
        var root = await _repositoryStore.GetRepositoryRootPathAsync(session.RepositoryId, cancellationToken);
        if (string.IsNullOrWhiteSpace(root))
        {
            return Fail("RepositoryNotIndexed", "Repository root was not found.");
        }

        if (!RepositoryPathGuard.TryResolve(root, file, out var absolute, out var pathError))
        {
            return Fail("ValidationError", pathError);
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            if (!IOFile.Exists(absolute)) return Fail("FileMissing", $"file missing: {file}", $"{file}#");
            var fileRequest = new ProtocolRequest(request.Version, "READ_FILE", session.Id.Value.ToString(),
                JsonSerializer.SerializeToElement(new { query = file, full = false, range = $"1:{int.MaxValue}" }), request.AgentLabel);
            return await HandleReadFileAsync(fileRequest, session, cancellationToken);
        }

        var resolution = await ReadableSymbolResolver.ResolveAsync(_repositoryStore, root, name, cancellationToken);
        var inFile = resolution.Candidates
            .Where(candidate => candidate.FilePath is not null
            && RepositoryPathGuard.TryResolve(root, candidate.FilePath.Replace('\\', '/'), out var candidatePath, out _)
            && string.Equals(candidatePath, absolute, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            .DistinctBy(candidate => candidate.Id)
            .ToList();

        if (inFile.Count == 0)
        {
            var suggestions = (await FileSymbolNamesAsync(file, cancellationToken))
                .Select(symbol => $"devx read section \"{file}#{symbol}\"")
                .Take(5)
                .ToList();
            if (suggestions.Count == 0)
            {
                suggestions = resolution.Candidates
                    .Select(candidate => $"devx read method \"{candidate.QualifiedName}\"")
                    .Take(5)
                    .ToList();
            }

            return Fail("NotFound", $"'{name}' is not a symbol in '{file}'.", $"{file}#{name}", suggestions);
        }

        if (inFile.Count > 1)
        {
            return Fail(
                "AmbiguousMatch",
                $"'{name}' matched multiple symbols in '{file}'.",
                $"{file}#{name}",
                inFile.Select(candidate => $"devx read method \"{candidate.QualifiedName}\""));
        }

        var symbol = inFile[0];
        var symbolRequest = new ProtocolRequest(request.Version, "READ_METHOD", session.Id.Value.ToString(),
            JsonSerializer.SerializeToElement(new { query = symbol.QualifiedName }), request.AgentLabel);
        var response = await HandleReadMethodAsync(symbolRequest, session, cancellationToken);
        if (!response.Success || response.Data is not { ValueKind: JsonValueKind.Object } data)
        {
            return response;
        }

        // `read method` already returns the right bytes; the section caller only needs the
        // response to say which symbol it resolved to, which the metadata carries.
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in data.EnumerateObject()) fields[property.Name] = property.Value.Clone();
        fields["Metadata"] = JsonSerializer.SerializeToElement(new
        {
            Heading = symbol.Name,
            RequestedHeading = name,
            ResolvedVia = "section-symbol",
            ResolvedSymbol = symbol.QualifiedName,
            SymbolKind = symbol.Kind,
            Message = $"read section '{file}#{name}' resolved to {symbol.Kind.ToLowerInvariant()} {symbol.QualifiedName}",
        });
        return new ProtocolResponse(true, JsonSerializer.SerializeToElement(fields), response.Diagnostics, []);
    }

    private async Task<IReadOnlyList<string>> FileSymbolNamesAsync(string file, CancellationToken cancellationToken)
    {
        var snapshot = await _repositoryStore.GetFileSnapshotAsync(file, cancellationToken);
        if (snapshot is null) return [];
        return snapshot.Types.Select(type => type.Name)
            .Concat(snapshot.Methods.Select(method => method.Name))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToList();
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

        if (_repositoryStore is not IIndexFreshnessStore freshnessStore)
        {
            return Fail("IndexFreshnessUnavailable", "The repository store cannot durably track source/index freshness; patch was not applied.");
        }
        var affectedFiles = patch.Operations.Select(operation => NormalizePatchDirtyPath(root, operation.Path))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
        if (!await freshnessStore.TryMarkIndexFilesDirtyAsync(root, affectedFiles, cancellationToken))
        {
            return Fail("IndexDirty", "Patch was not applied because this repository already has unreconciled source changes. Run 'devx index <root>' first.");
        }

        var (result, error) = _patchLifecycleService.Apply(root, patch);
        if (error is not null)
        {
            if (error.Code is "PatchCommitOutcomeUncertain" or "PatchRollbackOutcomeUncertain")
            {
                await freshnessStore.MarkIndexRepositoryDirtyAsync(root, affectedFiles, cancellationToken);
            }
            else
            {
                await freshnessStore.ClearDirtyIndexFilesAsync(root, affectedFiles, cancellationToken);
            }
            return Fail(error.Code, error.Message);
        }

        var appliedResult = result!;
        var refreshFailure = _refreshIndexAfterEdit ? await RefreshIndexAsync(root, affectedFiles, cancellationToken) : null;
        if (refreshFailure is not null)
        {
            var pending = await freshnessStore.GetDirtyIndexRepositoryAsync(root, cancellationToken);
            var files = pending is null ? "unknown files" : string.Join(", ", pending.DirtyFiles);
            return Fail("IndexRefreshFailedDirty", $"Patch source changes are applied, but indexed operations are blocked until reconciliation succeeds for {files}: {refreshFailure}. Run 'devx index <root>'.");
        }
        return new ProtocolResponse(true, JsonSerializer.SerializeToElement(appliedResult), appliedResult.ValidationErrors, []);
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

    private static string NormalizePatchDirtyPath(string root, string path)
    {
        if (RepositoryPathGuard.TryResolve(root, path, out var absolutePath, out _))
            return Path.GetRelativePath(root, absolutePath).Replace('\\', '/');
        return path.Replace('\\', '/');
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

        if (_repositoryStore is not IIndexFreshnessStore freshnessStore)
        {
            return Fail("IndexFreshnessUnavailable", "The repository store cannot durably track source/index freshness; rollback was not applied.");
        }
        var affectedFiles = patch.Operations.Select(operation => NormalizePatchDirtyPath(root, operation.Path))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
        if (!await freshnessStore.TryMarkIndexFilesDirtyAsync(root, affectedFiles, cancellationToken))
        {
            return Fail("IndexDirty", "Rollback was not applied because this repository already has unreconciled source changes. Run 'devx index <root>' first.");
        }

        var (response, error) = _patchLifecycleService.Rollback(root, patch);
        if (error is not null)
        {
            if (error.Code is "PatchCommitOutcomeUncertain" or "PatchRollbackOutcomeUncertain")
                await freshnessStore.MarkIndexRepositoryDirtyAsync(root, affectedFiles, cancellationToken);
            else
                await freshnessStore.ClearDirtyIndexFilesAsync(root, affectedFiles, cancellationToken);
            return Fail(error.Code, error.Message);
        }

        var refreshFailure = _refreshIndexAfterEdit ? await RefreshIndexAsync(root, affectedFiles, cancellationToken) : null;
        if (refreshFailure is not null)
        {
            return Fail("IndexRefreshFailedDirty", $"Patch rollback source changes are applied, but indexed operations are blocked until reconciliation succeeds: {refreshFailure}. Run 'devx index <root>'.");
        }
        return new ProtocolResponse(true, JsonSerializer.SerializeToElement(response), [], []);
    }

    /// <summary>
    /// Bring the index back in line with the files a write just touched, so the next read of the
    /// new text sees the new text. A file the write created is indexed; a file it deleted is
    /// dropped, because <c>ReindexFilesAsync</c> would only report it missing and leave the index
    /// pointing at a document that is gone. Failed refreshes remain fenced and are reported.
    /// </summary>
    private async Task<string?> RefreshIndexAsync(string root, IReadOnlyList<string> filesAffected, CancellationToken cancellationToken)
    {
        if (!_refreshIndexAfterEdit || filesAffected is null || filesAffected.Count == 0)
        {
            return "automatic index refresh is disabled or no affected paths were provided";
        }

        var present = new List<string>();
        var removed = new List<string>();
        foreach (var file in filesAffected.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
        {
            var absolute = Path.GetFullPath(Path.Combine(root, file.Replace('\\', '/')));
            if (!absolute.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            {
                return $"affected path '{file}' is outside the repository root";
            }
            (System.IO.File.Exists(absolute) ? present : removed).Add(file);
        }

        var indexer = new RepositoryIndexingService(_repositoryStore);

        // One file at a time, each guarded: a file with no owning project (a script beside the
        // solution, an unsupported extension) must not stop the others from being refreshed, and
        // none of it may fail a write that already succeeded.
        foreach (var file in present)
        {
            var failure = await RefreshOneAsync(async () =>
                {
                    var result = await indexer.ReindexFilesAsync(root, [file], cancellationToken);
                    return result.SkippedFiles == 0 ? null : string.Join("; ", result.SkippedFilePaths.Select(item => item.Reason));
                });
            if (failure is not null)
            {
                return failure;
            }
        }
        foreach (var file in removed)
        {
            var failure = await RefreshOneAsync(async () =>
                {
                    await indexer.RemoveFileAsync(root, file, cancellationToken);
                    return null;
                });
            if (failure is not null)
            {
                return failure;
            }
        }

        if (_repositoryStore is not IIndexFreshnessStore freshnessStore) return "repository store cannot confirm index freshness";
        var pending = await freshnessStore.GetDirtyIndexRepositoryAsync(root, cancellationToken);
        return pending is null ? null : $"dirty paths remain: {string.Join(", ", pending.DirtyFiles)}";
    }

    private static async Task<string?> RefreshOneAsync(Func<Task<string?>> refresh)
    {
        try
        {
            return await refresh();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException or Microsoft.Data.Sqlite.SqliteException)
        {
            return exception.Message;
        }
    }

    private static ProtocolResponse Fail(string code, string message, string? attemptedName = null, IEnumerable<string>? suggestions = null)
        => new(false, null, [], [new ProtocolError(code, message, attemptedName, suggestions?.Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList())]);

    private static List<SymbolSearchResult> RankCandidates(IEnumerable<SymbolSearchResult> candidates, string query)
        => candidates.OrderByDescending(candidate => string.Equals(candidate.Name, query.Split('.').Last(), StringComparison.Ordinal))
            .ThenBy(candidate => candidate.QualifiedName.Count(character => character == '.') + 1)
            .ThenBy(candidate => candidate.FilePath, StringComparer.Ordinal)
            .Take(10).ToList();

    private async Task<ProtocolResponse> NotFoundWithSuggestionsAsync(
        string kind,
        string query,
        CancellationToken cancellationToken,
        string? message = null)
    {
        var suggestions = await ClosestNamesAsync(query, cancellationToken);
        // A dotted miss is usually one prefix too long -- a Python module path pasted back
        // from `search`. Search again on the last segment so the answer is still a list of
        // names that `read` accepts, rather than an empty suggestion list.
        if (suggestions.Count == 0 && query.Contains('.'))
        {
            suggestions = await ClosestNamesAsync(query.Split('.').Last(), cancellationToken);
        }
        // Distinct/Take here too: Fail() clamps the structured list, but the sentence below is
        // built from the raw names and was printing the same suggestion twice.
        suggestions = suggestions.Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList();
        var suffix = suggestions.Count == 0 ? string.Empty : $" Did you mean: {string.Join(", ", suggestions)}?";
        return Fail("NotFound", (message ?? $"{kind} '{query}' was not found.") + suffix, query, suggestions);
    }

    private static bool RequiresFreshIndex(string operation) => operation is
        ProtocolOperations.SearchSymbol or ProtocolOperations.SearchText or ProtocolOperations.GetFileSnapshot or
        ProtocolOperations.GetMethodSummary or ProtocolOperations.GetMethod or ProtocolOperations.GetTypeOperation or
        ProtocolOperations.GetCallers or ProtocolOperations.GetCallees or ProtocolOperations.GetReferences or
        ProtocolOperations.Patch or ProtocolOperations.PatchRollback or "PATCH_PREVIEW" or "PATCH_VALIDATE" or
        "READ_METHOD" or "READ_SECTION" or "READ_TYPE";

    private async Task<List<string>> ClosestNamesAsync(string query, CancellationToken cancellationToken)
        => _repositoryStore is ISymbolStore symbols
            ? (await symbols.FindClosestSymbolsAsync(query, cancellationToken: cancellationToken))
                .Select(symbol => symbol.QualifiedName)
                .Where(name => !string.Equals(name, query, StringComparison.OrdinalIgnoreCase))
                .ToList()
            : [];

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

    private static string? ReadString(JsonElement payload, string propertyName)
        => payload.ValueKind == JsonValueKind.Object
           && payload.TryGetProperty(propertyName, out var element)
           && element.ValueKind == JsonValueKind.String
            ? element.GetString()
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

        // '595-615' and '595:615' both name a line range. Only ':' was accepted, so LG1's
        // "595-615" -- the spelling an editor shows -- was rejected outright and there was no
        // line-range read over MCP at all. Accept both; trim so ' 595 - 615 ' works too.
        var separator = range.Contains(':') ? ':' : range.Contains('-') ? '-' : '\0';
        if (separator == '\0')
        {
            return false;
        }

        var parts = range.Split(separator, 2);
        return parts.Length == 2
            && int.TryParse(parts[0].Trim(), out startLine)
            && int.TryParse(parts[1].Trim(), out endLine)
            && startLine > 0
            && endLine >= startLine;
    }

    private static bool TryGetBoolean(JsonElement payload, string propertyName)
        => payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty(propertyName, out var element)
            && element.ValueKind == JsonValueKind.True;

    /// <summary>
    /// The real runtime context when discovery found one, and a stand-in for a repository that
    /// declares its own command when it did not. Discovery can legitimately find nothing to run
    /// -- a documentation tree, or tests driven by a script DevX cannot guess -- and the
    /// repository's own answer in `.devx/config.json` is then the only answer there is.
    /// Refusing before reading it is the old behaviour that sent an agent to Bash.
    /// </summary>
    private static (RuntimeContext Context, RuntimeTargetDescriptor Target) ResolvedOrConfigured(
        string root, RuntimeContextResolution resolution, string capability)
    {
        if (resolution is { Success: true, Context: not null, SelectedTarget: not null })
        {
            return (resolution.Context, resolution.SelectedTarget);
        }

        return (
            new RuntimeContext(root, root, DevXCommandConfig.RelativePath, "configured", "default"),
            new RuntimeTargetDescriptor(
                "configured",
                $"{capability} command declared in {DevXCommandConfig.RelativePath}",
                DevXCommandConfig.RelativePath,
                "Healthy",
                []));
    }

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
