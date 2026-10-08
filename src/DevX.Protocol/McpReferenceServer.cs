using System.Text.Json;
using System.Text.Json.Nodes;
using DevX.Domain;
using DevX.Indexer;

namespace DevX.Protocol;

public sealed class McpReferenceServer
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IProtocolEngine _protocolEngine;
    private readonly ISessionStore _sessionStore;
    private readonly IRepositoryStore _repositoryStore;
    private readonly string? _defaultSessionId;
    private readonly Func<string[], CancellationToken, Task<IndexRefreshResult>>? _reindexFilesAsync;
    private readonly Func<CancellationToken, Task<RepositoryIndexResult>>? _indexRepositoryAsync;
    private readonly Func<CancellationToken, Task>? _initializeStorageAsync;
    private readonly string _version;
    private readonly Func<string, VerifyAfterEdit>? _verifyAfterEditFactory;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<string>>>? _changedFilesAsync;
    private readonly TextReplaceService _textReplaceService = new();

    public McpReferenceServer(
        IProtocolEngine protocolEngine,
        ISessionStore sessionStore,
        IRepositoryStore repositoryStore,
        string? defaultSessionId = null,
        Func<string[], CancellationToken, Task<IndexRefreshResult>>? reindexFilesAsync = null,
        string version = "1.0",
        Func<CancellationToken, Task<RepositoryIndexResult>>? indexRepositoryAsync = null,
        Func<CancellationToken, Task>? initializeStorageAsync = null,
        Func<string, VerifyAfterEdit>? verifyAfterEditFactory = null,
        Func<string, CancellationToken, Task<IReadOnlyList<string>>>? changedFilesAsync = null)
    {
        _changedFilesAsync = changedFilesAsync;
        _protocolEngine = protocolEngine;
        _sessionStore = sessionStore;
        _repositoryStore = repositoryStore;
        _defaultSessionId = defaultSessionId;
        _reindexFilesAsync = reindexFilesAsync;
        _indexRepositoryAsync = indexRepositoryAsync;
        _initializeStorageAsync = initializeStorageAsync;
        _verifyAfterEditFactory = verifyAfterEditFactory;
        _version = version;
    }

    public async Task<McpInitializeResponse> InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await RefreshStaleIndexAsync(cancellationToken);
        return new McpInitializeResponse(
            "2025-06-18",
            new McpServerInfo("devx", _version),
            BuildTools());
    }

    /// <summary>
    /// An agent's first move in the LG1 transcript was a full `index` of 3,292 files against
    /// an index that was already built. The server knew the answer and made the agent ask for
    /// it, so the check happens here instead: on start, anything the working tree has changed
    /// since the last index is refreshed incrementally. A failure (no git, a locked store) must
    /// never stop the server from starting -- it only means the index is left as it was.
    /// </summary>
    private async Task RefreshStaleIndexAsync(CancellationToken cancellationToken)
    {
        if (_reindexFilesAsync is null || _defaultSessionId is null || !Guid.TryParse(_defaultSessionId, out var sessionId)) return;
        try
        {
            if (_initializeStorageAsync is not null) await _initializeStorageAsync(cancellationToken);
            var session = await _sessionStore.GetSessionAsync(new GetSessionRequest(new SessionId(sessionId)), cancellationToken);
            if (session is null) return;
            var root = await _repositoryStore.GetRepositoryRootPathAsync(session.RepositoryId, cancellationToken);
            if (string.IsNullOrWhiteSpace(root)) return;
            var changed = _changedFilesAsync is null ? [] : await _changedFilesAsync(root, cancellationToken);
            if (changed.Count > 0) await _reindexFilesAsync(changed.ToArray(), cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // Nothing to report to: initialize has no channel for a warning, and a stale index
            // is a smaller problem than a server that will not start.
        }
    }

    public Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<McpToolDescriptor>>(BuildTools());
    }

    public async Task<McpToolCallResult> CallToolAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (_defaultSessionId is not null && arguments.ValueKind == JsonValueKind.Object)
        {
            var withSession = JsonNode.Parse(arguments.GetRawText())!.AsObject();
            withSession["sessionId"] = _defaultSessionId;
            arguments = JsonSerializer.SerializeToElement(withSession, JsonOptions);
        }
        if (RequiresIndex(toolName) && await IsRepositoryUnindexedAsync(arguments, cancellationToken))
        {
            const string hint = "repository not indexed: call the `index` tool first (omit `files` to index the whole repository)";
            return AgentFormat(arguments) == "json"
                ? new McpToolCallResult(true, JsonSerializer.SerializeToElement(new { error = hint }, JsonOptions))
                : new McpToolCallResult(true, JsonSerializer.SerializeToElement(new { success = false, text = $"error: {hint}" }, JsonOptions));
        }
        var dispatched = toolName switch
        {
            "search" => await ExecuteSearchAsync(arguments, cancellationToken),
            "read" => await ExecuteReadAsync(arguments, cancellationToken),
            "snapshot" => await ExecuteQueryToolAsync(ProtocolOperations.GetFileSnapshot, arguments, cancellationToken, AgentFormat(arguments)),
            "index" => await IndexFilesAsync(arguments, cancellationToken),
            "edit.replace" => await ReplaceTextAsync(arguments, cancellationToken),
            "callers" => await ExecuteQueryToolAsync(ProtocolOperations.GetCallers, arguments, cancellationToken, AgentFormat(arguments)),
            "callees" => await ExecuteQueryToolAsync(ProtocolOperations.GetCallees, arguments, cancellationToken, AgentFormat(arguments)),
            "references" => await ExecuteQueryToolAsync(ProtocolOperations.GetReferences, arguments, cancellationToken, AgentFormat(arguments)),
            "patch.preview" => await ExecuteProtocolAsync("PATCH_PREVIEW", RequiredSessionId(arguments), RequiredObject(arguments, "patch"), cancellationToken),
            "build" => await ExecuteProtocolAsync(ProtocolOperations.RunBuild, RequiredSessionId(arguments), BuildRuntimePayload(arguments), cancellationToken, AgentFormat(arguments)),
            "test" => await ExecuteProtocolAsync(ProtocolOperations.RunTests, RequiredSessionId(arguments), BuildTestPayload(arguments), cancellationToken, AgentFormat(arguments)),
            "patch.validate" => await ExecuteProtocolAsync(
                "PATCH_VALIDATE",
                RequiredSessionId(arguments),
                RequiredObject(arguments, "patch"),
                cancellationToken),
            "patch.apply" => await ExecuteProtocolAsync(
                ProtocolOperations.Patch,
                RequiredSessionId(arguments),
                RequiredObject(arguments, "patch"),
                cancellationToken),
            "report" => await GetReportAsync(RequiredSessionId(arguments), cancellationToken),
            _ => new McpToolCallResult(true, JsonSerializer.SerializeToElement(new { error = $"Unknown MCP tool '{toolName}'." }, JsonOptions))
        };
        return NormalizeToolResult(dispatched, arguments);
    }

    // One shape for every tool: the compact agent text, on success and on failure alike.
    // Some tools already returned that and others returned a raw JSON envelope, so an agent
    // met a one-line success from `search` and several thousand bytes of {"error":"..."}
    // from the next tool -- and raw JSON is exactly what it asked to avoid by not passing
    // format:json. Errors keep the `error: ` prefix the CLI uses.
    private static McpToolCallResult NormalizeToolResult(McpToolCallResult result, JsonElement arguments)
    {
        if (AgentFormat(arguments) == "json") return result;
        if (result.Content.ValueKind == JsonValueKind.Object
            && result.Content.TryGetProperty("text", out var compact)
            && compact.ValueKind == JsonValueKind.String)
        {
            return result;
        }
        return new McpToolCallResult(result.IsError, JsonSerializer.SerializeToElement(
            new { success = !result.IsError, text = CompactToolText(result.Content) }, JsonOptions));
    }

    private static string CompactToolText(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Object) return content.GetRawText();
        if (content.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
        {
            return $"error: {error.GetString()}";
        }
        var lines = new List<string>();
        foreach (var property in content.EnumerateObject())
        {
            // A nested value is summarised, not dumped: the report is the one tool whose
            // payload is a metrics blob, and an agent that wants all of it says format:json.
            var rendered = property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                ? property.Value.GetRawText()
                : property.Value.ToString();
            if (rendered.Length > 200) rendered = rendered[..200] + "...";
            lines.Add($"{property.Name}: {rendered}");
        }
        return string.Join("\n", lines);
    }

    private async Task<McpToolCallResult> ExecuteProtocolAsync(string operation, string sessionId, JsonElement payload, CancellationToken cancellationToken, string format = "json")
    {
        var response = await _protocolEngine.ExecuteAsync(new ProtocolRequest("1.0", operation, sessionId, payload), cancellationToken);
        return FormatResult(response, format);
    }

    private static string AgentFormat(JsonElement arguments)
        => TryGetString(arguments, "format", out var format) && format.Equals("json", StringComparison.OrdinalIgnoreCase) ? "json" : "agent";

    private static McpToolCallResult FormatResult(ProtocolResponse response, string format)
    {
        if (format == "json") return new McpToolCallResult(!response.Success, JsonSerializer.SerializeToElement(response, JsonOptions));
        return new McpToolCallResult(!response.Success, JsonSerializer.SerializeToElement(new { success = response.Success, text = AgentText(response) }, JsonOptions));
    }

    internal static string AgentText(ProtocolResponse response)
    {
        if (response.Data is { ValueKind: JsonValueKind.Object } executionData)
        {
            var execution = TryGetPropertyIgnoreCase(executionData, "execution", out var executionElement)
                ? executionElement.Deserialize<ExecutionRecord>(JsonOptions)
                : null;
            if (!string.IsNullOrWhiteSpace(execution?.Summary)) return execution.Summary;
        }
        if (!response.Success) return string.Join("\n", response.Errors.Select(error => $"error: {error.Message}"));
        if (response.Data is not { } data) return string.Empty;
        if (TryGetPropertyIgnoreCase(data, "symbols", out var symbols) && symbols.ValueKind == JsonValueKind.Array && symbols.GetArrayLength() > 0)
        {
            var hits = symbols.EnumerateArray().Take(10).Select(item => $"{Text(item, "kind").ToLowerInvariant()} {Text(item, "qualifiedName")} {RelativePath(Text(item, "filePath"))}:{Text(item, "startLine")}-{Text(item, "endLine")}").ToList();
            if (symbols.GetArrayLength() > hits.Count) hits.Add($"... {symbols.GetArrayLength() - hits.Count} more");
            return string.Join("\n", hits);
        }
        if (TryGetPropertyIgnoreCase(data, "contentMatches", out var matches) && matches.ValueKind == JsonValueKind.Array)
        {
            var hits = matches.EnumerateArray().Take(10).Select(item => { var preview = Text(item, "preview").Trim(); return $"{RelativePath(Text(item, "path"))}:{Text(item, "line")}: {preview[..Math.Min(120, preview.Length)]}"; }).ToList();
            if (matches.GetArrayLength() > hits.Count) hits.Add($"... {matches.GetArrayLength() - hits.Count} more");
            return string.Join("\n", hits);
        }
        foreach (var collection in new[] { "callers", "callees", "references" })
        {
            if (!TryGetPropertyIgnoreCase(data, collection, out var items)) continue;
            if (items.ValueKind == JsonValueKind.Object && TryGetPropertyIgnoreCase(items, collection, out var nested)) items = nested;
            if (items.ValueKind == JsonValueKind.Array)
                return string.Join("\n", items.EnumerateArray().Select(item => $"{RelativePath(Text(item, "filePath"))}:{Text(item, collection == "references" ? "lineNumber" : "callSiteLine")}: {Text(item, "qualifiedName")}"));
        }
        if (TryGetPropertyIgnoreCase(data, "snapshot", out var snapshot))
        {
            var lines = new List<string> { $"{RelativePath(Text(snapshot, "path"))} ({Text(snapshot, "language")}, {Text(snapshot, "approximateLoc")} lines)" };
            if (TryGetPropertyIgnoreCase(snapshot, "types", out var types) && types.ValueKind == JsonValueKind.Array)
                lines.AddRange(types.EnumerateArray().Where(type => Text(type, "startLine").Length > 0 && Text(type, "endLine").Length > 0).Select(type => $"type {Text(type, "name")} :{Text(type, "startLine")}-{Text(type, "endLine")}"));
            if (TryGetPropertyIgnoreCase(snapshot, "methods", out var methods) && methods.ValueKind == JsonValueKind.Array)
                lines.AddRange(methods.EnumerateArray().Where(method => Text(method, "startLine").Length > 0 && Text(method, "endLine").Length > 0).Select(method => $"  method {Text(method, "name")}({SignatureParameters(Text(method, "signature"))}) :{Text(method, "startLine")}-{Text(method, "endLine")}"));
            return string.Join("\n", lines);
        }
        if (TryGetPropertyIgnoreCase(data, "path", out var path) && TryGetPropertyIgnoreCase(data, "source", out var source))
        {
            var name = Text(data, "qualifiedName");
            var kind = Text(data, "kind");
            return $"{RelativePath(path.GetString() ?? "")}:{Text(data, "startLine")}-{Text(data, "endLine")}{(name.Length == 0 ? "" : $" {name}")}{(kind.Length == 0 ? "" : $" [{kind}]")}\n{source.GetString()}";
        }
        return data.GetRawText();
    }

    private static string Text(JsonElement element, string name)
        => TryGetPropertyIgnoreCase(element, name, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : string.Empty;

    private static string RelativePath(string path)
        => string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) ? path : Path.GetRelativePath(Environment.CurrentDirectory, path);

    private static string SignatureParameters(string signature)
    {
        var start = signature.IndexOf('(');
        var end = signature.IndexOf(')', start + 1);
        return start >= 0 && end > start ? signature[(start + 1)..end] : signature;
    }

    private async Task<McpToolCallResult> GetReportAsync(string sessionId, CancellationToken cancellationToken)
    {
        var session = await _sessionStore.GetSessionAsync(new GetSessionRequest(new SessionId(Guid.Parse(sessionId))), cancellationToken);
        if (session is null)
        {
            return new McpToolCallResult(true, JsonSerializer.SerializeToElement(new { error = "Session not found." }, JsonOptions));
        }

        var metrics = await _sessionStore.GetOperationMetricsAsync(session.Id, cancellationToken);
        var diagnostics = session.DiagnosticsHistory
            .Select(entry => JsonSerializer.Deserialize<DiagnosticRecord>(entry))
            .Where(item => item is not null)
            .Select(item => item!)
            .ToList();
        var fallbackEvents = metrics
            .Where(metric => metric.Operation == "fallback" && !string.IsNullOrWhiteSpace(metric.MetadataJson))
            .Select(metric =>
            {
                var metadata = JsonSerializer.Deserialize<McpFallbackEventMetadata>(metric.MetadataJson!);
                return metadata is null
                    ? null
                    : new
                    {
                        metadata.CapabilityId,
                        metadata.FailedCommand,
                        metadata.Reason,
                        metadata.FallbackAction,
                        metric.Timestamp
                    };
            })
            .Where(item => item is not null)
            .ToList();

        var payload = new
        {
            SessionSummary = new
            {
                SessionId = session.Id.Value.ToString(),
                RepositoryId = session.RepositoryId.Value.ToString(),
                WorkingSet = string.IsNullOrWhiteSpace(session.WorkingSet)
                    ? Array.Empty<string>()
                    : session.WorkingSet.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                SearchCount = session.SearchHistory.Count,
                ExpandedFileCount = session.ExpandedFiles.Count,
                ExpandedSymbolCount = session.ExpandedSymbols.Count
            },
            OperationLog = metrics.Select(metric => new
            {
                metric.Operation,
                metric.Timestamp,
                DurationMs = metric.Duration.TotalMilliseconds,
                metric.Success,
                metric.SessionId,
                metric.MetadataJson
            }).ToList(),
            RepositoryMetrics = new
            {
                IndexedFiles = await _repositoryStore.CountIndexedFilesAsync(session.RepositoryId, cancellationToken),
                SearchedFiles = session.SearchHistory.Count,
                ExpandedFiles = session.ExpandedFiles.Count,
                SnapshotsRequested = metrics.Count(metric => metric.Operation == "snapshot"),
                MethodsExpanded = session.ExpandedSymbols.Count,
                PatchesApplied = session.PatchHistory.Count
            },
            BuildTestMetrics = new
            {
                Available = metrics.Any(metric => metric.Operation is "build" or "test"),
                Reason = metrics.Any(metric => metric.Operation is "build" or "test") ? (string?)null : "Build/Test executed outside DevX.",
                BuildCount = metrics.Count(metric => metric.Operation == "build"),
                SuccessfulBuilds = metrics.Count(metric => metric.Operation == "build" && metric.Success),
                FailedBuilds = metrics.Count(metric => metric.Operation == "build" && !metric.Success),
                Warnings = diagnostics.Count(diagnostic => string.Equals(diagnostic.Severity, "warning", StringComparison.OrdinalIgnoreCase)),
                Errors = diagnostics.Count(diagnostic => string.Equals(diagnostic.Severity, "error", StringComparison.OrdinalIgnoreCase)),
                TestRuns = metrics.Count(metric => metric.Operation == "test"),
                Passed = 0,
                Failed = 0,
                Skipped = 0
            },
            FallbackMetrics = new
            {
                TotalFallbacks = fallbackEvents.Count,
                FrequencyByCapability = fallbackEvents
                    .GroupBy(item => item!.CapabilityId, StringComparer.OrdinalIgnoreCase)
                    .Select(group => new { CapabilityId = group.Key, Count = group.Count() })
                    .OrderByDescending(item => item.Count)
                    .ThenBy(item => item.CapabilityId, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                Events = fallbackEvents
            },
            LlmMetrics = "unavailable",
            EfficiencySummary = new
            {
                TotalOperations = metrics.Count,
                TotalDurationMs = metrics.Sum(metric => metric.Duration.TotalMilliseconds),
                RepairIterations = metrics.Count(metric => !metric.Success)
            }
        };

        return new McpToolCallResult(false, JsonSerializer.SerializeToElement(payload, JsonOptions));
    }

    private static JsonElement BuildReadPayload(JsonElement arguments)
    {
        var target = RequiredAliasString(arguments, "target", "query");
        var readKind = ReadKind(arguments);
        if (readKind == "section")
        {
            var separator = target.LastIndexOf('#');
            if (separator > 0 && separator < target.Length - 1)
                return JsonSerializer.SerializeToElement(new { file = target[..separator], heading = target[(separator + 1)..] }, JsonOptions);
        }
        const string queryName = "query";
        var payload = new Dictionary<string, object?>
        {
            [queryName] = target
        };
        var hasExplicitFull = false;
        var hasExplicitRange = false;

        if (TryGetString(arguments, "range", out var range))
        {
            payload["range"] = range;
            hasExplicitRange = true;
        }

        if (TryGetBoolean(arguments, "full", out var full))
        {
            payload["full"] = full;
            hasExplicitFull = true;
        }

        if (readKind == "file" && !hasExplicitFull && !hasExplicitRange)
        {
            // MCP file reads default to the full file unless the caller asks for a slice.
            payload["full"] = true;
        }

        return JsonSerializer.SerializeToElement(payload, JsonOptions);
    }

    private static string GetSearchOperation(JsonElement arguments)
        => !TryGetString(arguments, "kind", out var kind) || kind.Equals("symbol", StringComparison.OrdinalIgnoreCase)
            ? ProtocolOperations.SearchSymbol
            : ProtocolOperations.SearchText;

    private static string GetReadOperation(JsonElement arguments)
        => ReadKind(arguments) switch
        {
            "file" => "READ_FILE",
            "method" => "READ_METHOD",
            "type" => "READ_TYPE",
            "section" => "READ_SECTION",
            _ => throw new InvalidOperationException("Unsupported read kind.")
        };

    private Task<McpToolCallResult> ExecuteQueryToolAsync(string operation, JsonElement arguments, CancellationToken cancellationToken, string format = "json")
    {
        var payload = new Dictionary<string, string> { ["query"] = RequiredAliasString(arguments, "target", "query") };
        if (operation == ProtocolOperations.GetFileSnapshot && format == "agent") payload["format"] = "agent";
        return ExecuteProtocolAsync(operation, RequiredSessionId(arguments), JsonSerializer.SerializeToElement(payload, JsonOptions), cancellationToken, format);
    }

    private async Task<McpToolCallResult> IndexFilesAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!arguments.TryGetProperty("files", out var files) || files.ValueKind == JsonValueKind.Null || (files.ValueKind == JsonValueKind.Array && files.GetArrayLength() == 0))
        {
            if (_indexRepositoryAsync is null)
                return new McpToolCallResult(true, JsonSerializer.SerializeToElement(new { error = "Whole-repository indexing is unavailable." }, JsonOptions));

            // Asking for a whole-repository index when nothing has changed is a no-op the
            // server can answer itself. The agent that paid for this asked for 3,292 files it
            // already had, and the honest answer is that there is nothing to do.
            if (await CurrentIndexSummaryAsync(arguments, cancellationToken) is { } current)
            {
                return new McpToolCallResult(false, JsonSerializer.SerializeToElement(
                    new { success = true, filesIndexed = 0, alreadyCurrent = true, text = current },
                    JsonOptions));
            }

            var indexingResult = await _indexRepositoryAsync(cancellationToken);
            return new McpToolCallResult(false, JsonSerializer.SerializeToElement(new { success = true, filesIndexed = indexingResult.FilesIndexed, reason = indexingResult.Reason }, JsonOptions));
        }
        if (_reindexFilesAsync is null)
            return new McpToolCallResult(true, JsonSerializer.SerializeToElement(new { error = "Incremental indexing is unavailable." }, JsonOptions));
        if (files.ValueKind != JsonValueKind.Array || files.EnumerateArray().Any(file => file.ValueKind != JsonValueKind.String))
            return new McpToolCallResult(true, JsonSerializer.SerializeToElement(new { error = "Argument 'files' must be an array of paths." }, JsonOptions));

        var result = await _reindexFilesAsync(files.EnumerateArray().Select(file => file.GetString()!).ToArray(), cancellationToken);
        var failed = result.SkippedFilePaths?.Any(file => file.Reason.StartsWith("ERROR:", StringComparison.Ordinal)) == true;
        return new McpToolCallResult(failed, JsonSerializer.SerializeToElement(new { success = !failed, result }, JsonOptions));
    }

    /// <summary>
    /// "already current (N files)" when the working tree has nothing git reports as changed and
    /// the index holds files, null when a re-index is genuinely needed. Git being unavailable
    /// (a plain directory, no repository) means DevX cannot tell, and the answer to "cannot
    /// tell" is to do the work, not to claim it is already done.
    /// </summary>
    private async Task<string?> CurrentIndexSummaryAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (_changedFilesAsync is null) return null;
        var root = await TryGetRepositoryRootAsync(arguments, cancellationToken);
        if (string.IsNullOrWhiteSpace(root)) return null;
        try
        {
            var changed = await _changedFilesAsync(root, cancellationToken);
            if (changed.Count > 0) return null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            return null;
        }

        var indexed = await CountIndexedFilesAsync(arguments, cancellationToken);
        return indexed > 0 ? $"already current ({indexed} files)" : null;
    }

    private async Task<string?> TryGetRepositoryRootAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!TryGetString(arguments, "sessionId", out var sessionId) || !Guid.TryParse(sessionId, out var parsed)) return null;
        var session = await _sessionStore.GetSessionAsync(new GetSessionRequest(new SessionId(parsed)), cancellationToken);
        if (session is null) return null;
        var root = await _repositoryStore.GetRepositoryRootPathAsync(session.RepositoryId, cancellationToken);
        return string.IsNullOrWhiteSpace(root) ? null : root;
    }

    private async Task<int> CountIndexedFilesAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!TryGetString(arguments, "sessionId", out var sessionId) || !Guid.TryParse(sessionId, out var parsed)) return 0;
        var session = await _sessionStore.GetSessionAsync(new GetSessionRequest(new SessionId(parsed)), cancellationToken);
        if (session is null) return 0;
        return await _repositoryStore.CountIndexedFilesAsync(session.RepositoryId, cancellationToken);
    }

    private async Task<McpToolCallResult> ReplaceTextAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var session = await _sessionStore.GetSessionAsync(new GetSessionRequest(new SessionId(Guid.Parse(RequiredSessionId(arguments)))), cancellationToken);
        if (session is null) return new(true, JsonSerializer.SerializeToElement(new { error = "Session not found." }, JsonOptions));
        var root = await _repositoryStore.GetRepositoryRootPathAsync(session.RepositoryId, cancellationToken);
        if (string.IsNullOrEmpty(root)) return new(true, JsonSerializer.SerializeToElement(new { error = "Repository not found." }, JsonOptions));
        var result = _textReplaceService.Apply(root, RequiredString(arguments, "file"), RequiredString(arguments, "old"), RequiredString(arguments, "new"), TryGetBoolean(arguments, "all", out var all) && all, arguments.TryGetProperty("occurrence", out var occurrence) && occurrence.TryGetInt32(out var n) ? n : null);
        if (result.Success && _reindexFilesAsync is not null) await _reindexFilesAsync([RequiredString(arguments, "file")], cancellationToken);
        if (!result.Success) return new(true, JsonSerializer.SerializeToElement(new { text = result.Message }, JsonOptions));
        if (!TryGetBoolean(arguments, "verify", out var verify)) verify = true;
        if (!verify) return new(false, JsonSerializer.SerializeToElement(new { text = result.Message + (result.Diff.Length == 0 ? "" : "\n" + result.Diff) }, JsonOptions));

        var service = new DotnetBuildTestService();
        var symbolStore = _repositoryStore as ISymbolStore;
        var searchService = symbolStore is null ? null : new SearchService(_repositoryStore, symbolStore);
        var verifier = _verifyAfterEditFactory?.Invoke(root) ?? new VerifyAfterEdit(
            file => VerifyAfterEdit.RunBuildAsync(service, root, file, cancellationToken),
            files => service.RunTestsAsync(root, files, TimeSpan.FromSeconds(300), cancellationToken),
            file => symbolStore is not null && searchService is not null
                ? VerifyAfterEdit.FindRelevantTestFiles(root, file, symbolStore, searchService)
                : VerifyAfterEdit.FindRelevantTestFiles(root, file));
        var verification = await verifier.RunAsync(RequiredString(arguments, "file"));
        var verifyData = new { Build = new { verification.Build.Success, verification.Build.Summary, verification.Build.Diagnostics }, Tests = verification.Tests is null ? null : new { verification.Tests.Success, verification.Tests.Summary, verification.Tests.Diagnostics } };
        if (TryGetString(arguments, "format", out var format) && format == "json")
            return new(false, JsonSerializer.SerializeToElement(new { text = result.Message + (result.Diff.Length == 0 ? "" : "\n" + result.Diff), Verify = verifyData }, JsonOptions));
        var text = result.Message + (result.Diff.Length == 0 ? "" : "\n" + result.Diff) + "\nbuild: " + verification.Build.Summary;
        if (verification.Build.Success) text += "\ntests: " + (verification.Tests?.Summary ?? "PASS tests — skipped");
        else text += "\nedit kept; fix forward or revert";
        return new(false, JsonSerializer.SerializeToElement(new { text }, JsonOptions));
    }

    private async Task<bool> IsRepositoryUnindexedAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (_initializeStorageAsync is not null)
            await _initializeStorageAsync(cancellationToken);
        var session = await _sessionStore.GetSessionAsync(new GetSessionRequest(new SessionId(Guid.Parse(RequiredSessionId(arguments)))), cancellationToken);
        return session is null || await _repositoryStore.CountIndexedFilesAsync(session.RepositoryId, cancellationToken) == 0;
    }

    private static bool RequiresIndex(string toolName)
        => toolName is "search" or "read" or "snapshot" or "callers" or "callees" or "references" or "patch.preview" or "patch.validate" or "patch.apply";

    private static string RequiredAliasString(JsonElement arguments, string canonical, string alias)
        => TryGetString(arguments, canonical, out var value) ? value : RequiredString(arguments, alias);

    private static JsonElement BuildRuntimePayload(JsonElement arguments)
    {
        var payload = new Dictionary<string, string?>();
        if (TryGetString(arguments, "runtime", out var runtime)) payload["runtime"] = runtime;
        if (TryGetString(arguments, "target", out var target)) payload["target"] = target;
        return JsonSerializer.SerializeToElement(payload, JsonOptions);
    }

    private static JsonElement BuildTestPayload(JsonElement arguments)
    {
        var payload = new Dictionary<string, object?> { ["timeoutSeconds"] = 300 };
        if (TryGetString(arguments, "target", out var target)) payload["target"] = target;
        if (arguments.TryGetProperty("testFiles", out var files)) payload["testFiles"] = files.Clone();
        if (arguments.TryGetProperty("timeoutSeconds", out var timeout)) payload["timeoutSeconds"] = timeout.GetInt32();
        return JsonSerializer.SerializeToElement(payload, JsonOptions);
    }

    private static string ReadKind(JsonElement arguments)
    {
        var target = TryGetString(arguments, "target", out var name) ? name : string.Empty;
        var hasRange = TryGetString(arguments, "range", out _);
        var hasFull = TryGetBoolean(arguments, "full", out var full) && full;
        if (TryGetString(arguments, "kind", out var kind) && !kind.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var explicitKind = kind.ToLowerInvariant();
            // A range or a full read only means anything for a file, whatever the caller
            // called it. LG1 asked for lines 595-615 with kind=section and was told to use
            // 'file#heading' instead; the label was the wrong thing to argue about.
            if (explicitKind == "section" && !target.Contains('#') && (hasRange || hasFull)) return "file";
            return explicitKind;
        }
        if (target.Contains('#')) return "section";
        if (hasRange || hasFull) return "file";
        return LooksLikeFilePath(target) ? "file" : "method";
    }

    // Path.HasExtension("db.models.query.QuerySet.count") is true -- ".count" reads as an
    // extension -- so every Python qualified name `search` returned was classified as a file
    // and `read` answered "file missing: db.models.query.QuerySet.count". Only the bare
    // QuerySet.count worked later, once the agent gave up and went to grep. A name is a file
    // only when it looks like one.
    private static bool LooksLikeFilePath(string target)
    {
        if (target.Length == 0) return false;
        if (target.Contains(Path.DirectorySeparatorChar) || target.Contains('/')) return true;
        if (System.IO.File.Exists(target)) return true;
        return Path.GetExtension(target).ToLowerInvariant() is
            ".cs" or ".csproj" or ".sln" or ".py" or ".pyi" or ".md" or ".markdown" or ".json" or
            ".toml" or ".cfg" or ".ini" or ".txt" or ".yml" or ".yaml" or ".ts" or ".tsx" or ".js" or
            ".jsx" or ".mjs" or ".cjs" or ".java" or ".kt" or ".go" or ".rs" or ".rb" or ".php" or
            ".sh" or ".bash" or ".zsh" or ".sql" or ".html" or ".htm" or ".css" or ".scss" or ".xml";
    }


    private async Task<McpToolCallResult> ExecuteSearchAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var query = RequiredString(arguments, "query");
        var operation = GetSearchOperation(arguments);
        var implicitKind = !TryGetString(arguments, "kind", out _);
        var sessionId = RequiredSessionId(arguments);
        var response = await _protocolEngine.ExecuteAsync(new ProtocolRequest("1.0", operation, sessionId, JsonSerializer.SerializeToElement(new { query })), cancellationToken);
        if (implicitKind && operation == ProtocolOperations.SearchSymbol && response.Success && response.Data is { ValueKind: JsonValueKind.Object } data && TryGetPropertyIgnoreCase(data, "symbols", out var symbols) && symbols.ValueKind == JsonValueKind.Array && symbols.GetArrayLength() == 0)
            response = await _protocolEngine.ExecuteAsync(new ProtocolRequest("1.0", ProtocolOperations.SearchText, sessionId, JsonSerializer.SerializeToElement(new { query })), cancellationToken);
        var autoRead = string.Empty;
        if (AgentFormat(arguments) != "json" && operation == ProtocolOperations.SearchSymbol && response.Success && response.Data is { ValueKind: JsonValueKind.Object } searchData && TryGetPropertyIgnoreCase(searchData, "symbols", out var singleHits) && singleHits.ValueKind == JsonValueKind.Array && singleHits.GetArrayLength() == 1)
        {
            var hit = singleHits[0];
            var id = Text(hit, "id");
            var kind = Text(hit, "kind");
            if (id.Length > 0)
            {
                var readOperation = kind is "Class" or "Type" or "Record" or "Interface" or "Struct" or "Enum" ? "READ_TYPE" : "READ_METHOD";
                var read = await _protocolEngine.ExecuteAsync(new ProtocolRequest("1.0", readOperation, sessionId, JsonSerializer.SerializeToElement(new { id })), cancellationToken);
                // The hit line stays in the result; the source is appended, and only when the
                // span is small enough that an unasked-for read cannot flood the context.
                if (read.Success) autoRead = AutoReadText(read);
            }
        }
        if (autoRead.Length == 0) return FormatResult(response, AgentFormat(arguments));
        var combined = AgentText(response) + "\n" + autoRead;
        return new McpToolCallResult(!response.Success, JsonSerializer.SerializeToElement(new { success = response.Success, text = combined }, JsonOptions));
    }

    // Mirrors the CLI's AutoReadText: an explicit read always returns its source, this cap
    // only applies to source the agent did not ask for.
    private static string AutoReadText(ProtocolResponse read)
    {
        const int autoReadMaxLines = 80;
        var text = AgentText(read);
        if (read.Data is not { ValueKind: JsonValueKind.Object } data) return text;
        var head = text.Split('\n')[0];
        if (!int.TryParse(Text(data, "startLine"), out var start) || !int.TryParse(Text(data, "endLine"), out var end) || start <= 0 || end < start)
            return text;
        var span = end - start + 1;
        if (span <= autoReadMaxLines) return text;
        var body = text.Contains('\n') ? text[(text.IndexOf('\n') + 1)..] : string.Empty;
        var first = body.Split('\n').FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim() ?? string.Empty;
        return $"{head} {first}\nread to get {span} lines";
    }

    private async Task<McpToolCallResult> ExecuteReadAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if ((!arguments.TryGetProperty("targets", out var explicitTargets) || explicitTargets.ValueKind != JsonValueKind.Array)
            && TryGetString(arguments, "kind", out var explicitKind)
            && !explicitKind.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return await ExecuteProtocolAsync(
                GetReadOperation(arguments),
                RequiredSessionId(arguments),
                BuildReadPayload(arguments),
                cancellationToken,
                AgentFormat(arguments));
        }

        string[] targets = arguments.TryGetProperty("targets", out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!).ToArray()
            : new[] { RequiredAliasString(arguments, "target", "query") };
        var outputs = new List<string>();
        // A read that failed is an error: this path used to answer isError=false with an
        // error body, so a client counting failures saw a success.
        var failed = false;
        foreach (var target in targets)
        {
            var node = JsonNode.Parse(arguments.GetRawText())!.AsObject();
            node["target"] = target;
            node.Remove("targets");
            var item = JsonSerializer.SerializeToElement(node, JsonOptions);
            var operation = GetReadOperation(item);
            var response = await _protocolEngine.ExecuteAsync(new ProtocolRequest("1.0", operation, RequiredSessionId(item), BuildReadPayload(item)), cancellationToken);
            if (!response.Success) failed = true;
            if (targets.Length > 1) outputs.Add($"=== {target} ===");
            outputs.Add(AgentFormat(arguments) == "json" ? JsonSerializer.Serialize(response, JsonOptions) : AgentText(response));
        }
        return new McpToolCallResult(failed, JsonSerializer.SerializeToElement(new { text = string.Join("\n", outputs) }, JsonOptions));
    }

    private static string RequiredSessionId(JsonElement arguments)
        => RequiredString(arguments, "sessionId");

    private static JsonElement RequiredObject(JsonElement arguments, string propertyName)
    {
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Missing object argument '{propertyName}'.");
        }

        return property.Clone();
    }

    private static string RequiredString(JsonElement arguments, string propertyName)
    {
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"Missing string argument '{propertyName}'.");
        }

        return property.GetString()!;
    }

    private static bool TryGetString(JsonElement arguments, string propertyName, out string value)
    {
        value = string.Empty;
        return arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && (value = property.GetString() ?? string.Empty).Length > 0;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static bool TryGetBoolean(JsonElement arguments, string propertyName, out bool value)
    {
        value = false;
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.True)
        {
            value = true;
            return true;
        }

        if (property.ValueKind == JsonValueKind.False)
        {
            value = false;
            return true;
        }

        return false;
    }

    private static IReadOnlyList<McpToolDescriptor> BuildTools()
    {
        return
        [
            CreateTool("edit.replace", "Replace text", "Step 3: Use edit.replace for a simple text edit after search and read. Add all or occurrence to select matches. Edits are verified by default; pass verify:false to skip.", "edit.replace", new { type = "object", properties = new { file = new { type = "string" }, old = new { type = "string" }, @new = new { type = "string" }, all = new { type = "boolean" }, occurrence = new { type = "integer" }, verify = new { type = "boolean" }, format = new { type = "string" }, sessionId = new { type = "string" } }, required = new[] { "file", "old", "new" } }),
            CreateTool("search", "Search", "Step 1: Use search to find symbols or text in the indexed repository; it falls back to text when no symbol matches.", "search", new
            {
                type = "object",
                properties = new
                {
                    sessionId = new { type = "string" },
                    kind = new { type = "string", @enum = new[] { "symbol", "text" } },
                    query = new { type = "string" }
                },
                required = new[] { "query" }
            }),
            CreateTool("read", "Read", "Step 2: Use read to inspect a symbol, file, or section before editing.", "read", new
            {
                type = "object",
                properties = new
                {
                    sessionId = new { type = "string" },
                    kind = new { type = "string", @enum = new[] { "auto", "file", "method", "type", "section" } },
                    target = new { type = "string" },
                    targets = new { type = "array", items = new { type = "string" } },
                    range = new { type = "string" },
                    full = new { type = "boolean" }
                },
                required = Array.Empty<string>()
            }),
            CreateTool("patch.validate", "Patch Validate", "For structured or multi-file edits only. Simple text edits should use edit.replace.", "patch.validate", new
            {
                type = "object",
                properties = new
                {
                    sessionId = new { type = "string" },
                    patch = new { type = "object" }
                },
                required = new[] { "patch" }
            }),
            CreateTool("patch.apply", "Patch Apply", "For structured or multi-file edits only. Simple text edits should use edit.replace.", "patch.apply", new
            {
                type = "object",
                properties = new
                {
                    sessionId = new { type = "string" },
                    patch = new { type = "object" }
                },
                required = new[] { "patch" }
            }),
            CreateTool("patch.preview", "Patch Preview", "For structured or multi-file edits only. Simple text edits should use edit.replace.", "patch.validate", new { type = "object", properties = new { sessionId = new { type = "string" }, patch = new { type = "object" } }, required = new[] { "patch" } }),
            CreateQueryTool("snapshot", "Snapshot", "Get a source file snapshot.", "sessionId", "target"),
            CreateTool("index", "Index files", "Rarely needed: the server refreshes stale files on start and after every edit, and a whole-repository index that is already current answers 'already current'. Pass `files` only to force a re-index of paths you know changed.", "index", new { type = "object", properties = new { files = new { type = "array", items = new { type = "string" } }, sessionId = new { type = "string" } }, required = Array.Empty<string>() }),
            CreateQueryTool("callers", "Callers", "Find callers of a symbol.", "sessionId", "target"),
            CreateQueryTool("callees", "Callees", "Find callees of a symbol.", "sessionId", "target"),
            CreateQueryTool("references", "References", "Find references to a symbol.", "sessionId", "target"),
            CreateTool("build", "Build", "Run a configured build target.", "build", new { type = "object", properties = new { sessionId = new { type = "string" }, runtime = new { type = "string" }, target = new { type = "string" } }, required = Array.Empty<string>() }),
            CreateTool("test", "Test", "Step 4: Use test to run the configured build and test summary after editing.", "test", new { type = "object", properties = new { sessionId = new { type = "string" }, target = new { type = "string" }, testFiles = new { type = "array", items = new { type = "string" } }, timeoutSeconds = new { type = "integer" } }, required = Array.Empty<string>() }),
            CreateTool("report", "Report", "Return the current session report through the runtime capability layer.", "report", new
            {
                type = "object",
                properties = new
                {
                    sessionId = new { type = "string" }
                },
                required = Array.Empty<string>()
            })
        ];
    }

    private static McpToolDescriptor CreateTool(string name, string title, string description, string capabilityId, object schema)
    {
        var node = JsonSerializer.SerializeToNode(schema, JsonOptions)!.AsObject();
        var properties = node["properties"]?.AsObject();
        if (properties is not null && !properties.ContainsKey("format"))
            properties["format"] = JsonSerializer.SerializeToNode(new { type = "string", @enum = new[] { "agent", "json" } }, JsonOptions);
        return new(name, title, description, capabilityId, JsonSerializer.SerializeToElement(node, JsonOptions));
    }

    private static McpToolDescriptor CreateQueryTool(string name, string title, string description, string sessionProperty, string queryProperty)
        => CreateTool(name, title, description, name, new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                [sessionProperty] = new { type = "string" },
                [queryProperty] = new { type = "string" }
            },
            required = new[] { queryProperty }
        });

    private sealed record McpFallbackEventMetadata(
        string CapabilityId,
        string FailedCommand,
        string Reason,
        string FallbackAction);
}
