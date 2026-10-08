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
    private readonly TextReplaceService _textReplaceService = new();

    public McpReferenceServer(
        IProtocolEngine protocolEngine,
        ISessionStore sessionStore,
        IRepositoryStore repositoryStore,
        string? defaultSessionId = null,
        Func<string[], CancellationToken, Task<IndexRefreshResult>>? reindexFilesAsync = null,
        string version = "1.0",
        Func<CancellationToken, Task<RepositoryIndexResult>>? indexRepositoryAsync = null,
        Func<CancellationToken, Task>? initializeStorageAsync = null)
    {
        _protocolEngine = protocolEngine;
        _sessionStore = sessionStore;
        _repositoryStore = repositoryStore;
        _defaultSessionId = defaultSessionId;
        _reindexFilesAsync = reindexFilesAsync;
        _indexRepositoryAsync = indexRepositoryAsync;
        _initializeStorageAsync = initializeStorageAsync;
        _version = version;
    }

    public Task<McpInitializeResponse> InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new McpInitializeResponse(
            "2025-06-18",
            new McpServerInfo("devx", _version),
            BuildTools()));
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
            return new McpToolCallResult(true, JsonSerializer.SerializeToElement(new { error = "repository not indexed: call the `index` tool first (omit `files` to index the whole repository)" }, JsonOptions));
        return toolName switch
        {
            "search" => await ExecuteProtocolAsync(
                GetSearchOperation(arguments),
                RequiredSessionId(arguments),
                JsonSerializer.SerializeToElement(new
                {
                    query = RequiredString(arguments, "query")
                }),
                cancellationToken),
            "read" => await ExecuteProtocolAsync(
                GetReadOperation(arguments),
                RequiredSessionId(arguments),
                BuildReadPayload(arguments),
                cancellationToken),
            "snapshot" => await ExecuteQueryToolAsync(ProtocolOperations.GetFileSnapshot, arguments, cancellationToken),
            "index" => await IndexFilesAsync(arguments, cancellationToken),
            "edit.replace" => await ReplaceTextAsync(arguments, cancellationToken),
            "callers" => await ExecuteQueryToolAsync(ProtocolOperations.GetCallers, arguments, cancellationToken),
            "callees" => await ExecuteQueryToolAsync(ProtocolOperations.GetCallees, arguments, cancellationToken),
            "references" => await ExecuteQueryToolAsync(ProtocolOperations.GetReferences, arguments, cancellationToken),
            "patch.preview" => await ExecuteProtocolAsync("PATCH_PREVIEW", RequiredSessionId(arguments), RequiredObject(arguments, "patch"), cancellationToken),
            "build" => await ExecuteProtocolAsync(ProtocolOperations.RunBuild, RequiredSessionId(arguments), BuildRuntimePayload(arguments), cancellationToken),
            "test" => await ExecuteProtocolAsync(ProtocolOperations.RunTests, RequiredSessionId(arguments), BuildTestPayload(arguments), cancellationToken),
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
    }

    private async Task<McpToolCallResult> ExecuteProtocolAsync(string operation, string sessionId, JsonElement payload, CancellationToken cancellationToken)
    {
        var response = await _protocolEngine.ExecuteAsync(new ProtocolRequest("1.0", operation, sessionId, payload), cancellationToken);
        return new McpToolCallResult(!response.Success, JsonSerializer.SerializeToElement(response, JsonOptions));
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
        var queryName = readKind == "file" ? "query" : "query";
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
        => RequiredString(arguments, "kind").Equals("symbol", StringComparison.OrdinalIgnoreCase)
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

    private Task<McpToolCallResult> ExecuteQueryToolAsync(string operation, JsonElement arguments, CancellationToken cancellationToken)
        => ExecuteProtocolAsync(operation, RequiredSessionId(arguments), JsonSerializer.SerializeToElement(new { query = RequiredAliasString(arguments, "target", "query") }), cancellationToken);

    private async Task<McpToolCallResult> IndexFilesAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!arguments.TryGetProperty("files", out var files) || files.ValueKind == JsonValueKind.Null || (files.ValueKind == JsonValueKind.Array && files.GetArrayLength() == 0))
        {
            if (_indexRepositoryAsync is null)
                return new McpToolCallResult(true, JsonSerializer.SerializeToElement(new { error = "Whole-repository indexing is unavailable." }, JsonOptions));
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

    private async Task<McpToolCallResult> ReplaceTextAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var session = await _sessionStore.GetSessionAsync(new GetSessionRequest(new SessionId(Guid.Parse(RequiredSessionId(arguments)))), cancellationToken);
        if (session is null) return new(true, JsonSerializer.SerializeToElement(new { error = "Session not found." }, JsonOptions));
        var root = await _repositoryStore.GetRepositoryRootPathAsync(session.RepositoryId, cancellationToken);
        if (string.IsNullOrEmpty(root)) return new(true, JsonSerializer.SerializeToElement(new { error = "Repository not found." }, JsonOptions));
        var result = _textReplaceService.Apply(root, RequiredString(arguments, "file"), RequiredString(arguments, "old"), RequiredString(arguments, "new"), TryGetBoolean(arguments, "all", out var all) && all, arguments.TryGetProperty("occurrence", out var occurrence) && occurrence.TryGetInt32(out var n) ? n : null);
        if (result.Success && _reindexFilesAsync is not null) await _reindexFilesAsync([RequiredString(arguments, "file")], cancellationToken);
        return new(!result.Success, JsonSerializer.SerializeToElement(new { text = result.Message + (result.Diff.Length == 0 ? "" : "\n" + result.Diff) }, JsonOptions));
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
        => RequiredString(arguments, "kind").ToLowerInvariant();

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
            CreateTool("edit.replace", "Replace text", "Replace an exact text fragment in one file.", "edit.replace", new { type = "object", properties = new { file = new { type = "string" }, old = new { type = "string" }, @new = new { type = "string" }, all = new { type = "boolean" }, occurrence = new { type = "integer" }, sessionId = new { type = "string" } }, required = new[] { "file", "old", "new" } }),
            CreateTool("search", "Search", "Search indexed symbols or text through the runtime capability layer.", "search", new
            {
                type = "object",
                properties = new
                {
                    sessionId = new { type = "string" },
                    kind = new { type = "string", @enum = new[] { "symbol", "text" } },
                    query = new { type = "string" }
                },
                required = new[] { "kind", "query" }
            }),
            CreateTool("read", "Read", "Read file, method, or type content through the runtime capability layer.", "read", new
            {
                type = "object",
                properties = new
                {
                    sessionId = new { type = "string" },
                    kind = new { type = "string", @enum = new[] { "file", "method", "type", "section" } },
                    target = new { type = "string" },
                    range = new { type = "string" },
                    full = new { type = "boolean" }
                },
                required = new[] { "kind", "target" }
            }),
            CreateTool("patch.validate", "Patch Validate", "Validate a structured patch through the runtime capability layer.", "patch.validate", new
            {
                type = "object",
                properties = new
                {
                    sessionId = new { type = "string" },
                    patch = new { type = "object" }
                },
                required = new[] { "patch" }
            }),
            CreateTool("patch.apply", "Patch Apply", "Apply a structured patch through the runtime capability layer.", "patch.apply", new
            {
                type = "object",
                properties = new
                {
                    sessionId = new { type = "string" },
                    patch = new { type = "object" }
                },
                required = new[] { "patch" }
            }),
            CreateTool("patch.preview", "Patch Preview", "Preview a structured patch.", "patch.validate", new { type = "object", properties = new { sessionId = new { type = "string" }, patch = new { type = "object" } }, required = new[] { "patch" } }),
            CreateQueryTool("snapshot", "Snapshot", "Get a source file snapshot.", "sessionId", "target"),
            CreateTool("index", "Index files", "Index given paths or the whole repository when files is omitted.", "index", new { type = "object", properties = new { files = new { type = "array", items = new { type = "string" } }, sessionId = new { type = "string" } }, required = Array.Empty<string>() }),
            CreateQueryTool("callers", "Callers", "Find callers of a symbol.", "sessionId", "target"),
            CreateQueryTool("callees", "Callees", "Find callees of a symbol.", "sessionId", "target"),
            CreateQueryTool("references", "References", "Find references to a symbol.", "sessionId", "target"),
            CreateTool("build", "Build", "Run a configured build target.", "build", new { type = "object", properties = new { sessionId = new { type = "string" }, runtime = new { type = "string" }, target = new { type = "string" } }, required = Array.Empty<string>() }),
            CreateTool("test", "Test", "Run tests through the configured test service.", "test", new { type = "object", properties = new { sessionId = new { type = "string" }, target = new { type = "string" }, testFiles = new { type = "array", items = new { type = "string" } }, timeoutSeconds = new { type = "integer" } }, required = Array.Empty<string>() }),
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
        => new(name, title, description, capabilityId, JsonSerializer.SerializeToElement(schema, JsonOptions));

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
