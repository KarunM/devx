using System.Text.Json;
using DevX.Domain;

namespace DevX.Protocol;

public sealed class McpReferenceServer
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IProtocolEngine _protocolEngine;
    private readonly ISessionStore _sessionStore;
    private readonly IRepositoryStore _repositoryStore;

    public McpReferenceServer(IProtocolEngine protocolEngine, ISessionStore sessionStore, IRepositoryStore repositoryStore)
    {
        _protocolEngine = protocolEngine;
        _sessionStore = sessionStore;
        _repositoryStore = repositoryStore;
    }

    public Task<McpInitializeResponse> InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new McpInitializeResponse(
            "2025-06-18",
            new McpServerInfo("devx-reference-server", "1.0"),
            BuildTools()));
    }

    public Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<McpToolDescriptor>>(BuildTools());
    }

    public async Task<McpToolCallResult> CallToolAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken = default)
    {
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
        var target = RequiredString(arguments, "target");
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
            _ => throw new InvalidOperationException("Unsupported read kind.")
        };

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
            CreateTool("search", "Search", "Search indexed symbols or text through the runtime capability layer.", "search", new
            {
                type = "object",
                properties = new
                {
                    sessionId = new { type = "string" },
                    kind = new { type = "string", @enum = new[] { "symbol", "text" } },
                    query = new { type = "string" }
                },
                required = new[] { "sessionId", "kind", "query" }
            }),
            CreateTool("read", "Read", "Read file, method, or type content through the runtime capability layer.", "read", new
            {
                type = "object",
                properties = new
                {
                    sessionId = new { type = "string" },
                    kind = new { type = "string", @enum = new[] { "file", "method", "type" } },
                    target = new { type = "string" },
                    range = new { type = "string" },
                    full = new { type = "boolean" }
                },
                required = new[] { "sessionId", "kind", "target" }
            }),
            CreateTool("patch.validate", "Patch Validate", "Validate a structured patch through the runtime capability layer.", "patch.validate", new
            {
                type = "object",
                properties = new
                {
                    sessionId = new { type = "string" },
                    patch = new { type = "object" }
                },
                required = new[] { "sessionId", "patch" }
            }),
            CreateTool("patch.apply", "Patch Apply", "Apply a structured patch through the runtime capability layer.", "patch.apply", new
            {
                type = "object",
                properties = new
                {
                    sessionId = new { type = "string" },
                    patch = new { type = "object" }
                },
                required = new[] { "sessionId", "patch" }
            }),
            CreateTool("report", "Report", "Return the current session report through the runtime capability layer.", "report", new
            {
                type = "object",
                properties = new
                {
                    sessionId = new { type = "string" }
                },
                required = new[] { "sessionId" }
            })
        ];
    }

    private static McpToolDescriptor CreateTool(string name, string title, string description, string capabilityId, object schema)
        => new(name, title, description, capabilityId, JsonSerializer.SerializeToElement(schema, JsonOptions));

    private sealed record McpFallbackEventMetadata(
        string CapabilityId,
        string FailedCommand,
        string Reason,
        string FallbackAction);
}
