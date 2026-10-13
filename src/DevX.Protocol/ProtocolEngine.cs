using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using DevX.Application;
using DevX.Domain;
using DevX.Indexer;
using IOFile = System.IO.File;

namespace DevX.Protocol;

public sealed partial class ProtocolEngine : IProtocolEngine
{
    private readonly ISearchService _searchService;
    private readonly ISessionStore _sessionStore;
    private readonly IRepositoryStore _repositoryStore;
    private readonly DotnetBuildTestService _buildTestService;
    private readonly PatchLifecycleService _patchLifecycleService;
    private readonly RuntimeContextResolver _runtimeContextResolver;

    /// <summary>Whether a write re-indexes the files it touched (every write path re-indexes
    /// by default, `--no-index` opts out). An agent that edits and does not re-index leaves the
    /// index describing the old text, so the next `devx read` of what it just wrote can return
    /// the previous version.</summary>
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
}
