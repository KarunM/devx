using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using DevX.Application;
using DevX.Domain;
using DevX.Indexer;
using IOFile = System.IO.File;

namespace DevX.Protocol;

public sealed partial class ProtocolEngine
{
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
