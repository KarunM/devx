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
        var textBacked = await TryTextReferencesAsync(query, session, callersOnly: true, methods, TryGetBoolean(request.Payload, "full"), cancellationToken);
        if (textBacked is not null)
        {
            return textBacked;
        }

        var resolved = await ResolveCallTargetAsync(query, session, cancellationToken);
        if (resolved.Ambiguous is not null)
        {
            return resolved.Ambiguous;
        }

        if (resolved.Overloads.Count > 1 && _repositoryStore is DevX.Infrastructure.Storage.SQLite.SqliteStorageInitializer sqlite)
        {
            var overloads = await OverloadSymbolsAsync(resolved, cancellationToken);
            var groups = CallOverloads.Callers(overloads, await sqlite.ListMethodCandidatesAsync(overloads[0].RepositoryId, cancellationToken));
            var distinct = groups.SelectMany(group => group.Callers).DistinctBy(caller => caller.CallerMethodId).ToList();
            foreach (var caller in distinct)
            {
                await RecordHistoryAsync(() => _sessionStore.AddWorkingSetItemAsync(new AddWorkingSetItemRequest(session.Id, caller.CallerMethodId), cancellationToken));
            }

            return new ProtocolResponse(true, JsonSerializer.SerializeToElement(new CallersResponse(new CallerSummaryResponse(query, "Exact", null, distinct, null, groups))), [], []);
        }

        if (resolved.Query != query)
        {
            methods = await _repositoryStore.FindMethodSummariesAsync(resolved.Query, cancellationToken);
        }

        var callers = await _repositoryStore.FindCallersAsync(resolved.Query, cancellationToken);
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

        return new ProtocolResponse(true, JsonSerializer.SerializeToElement(new CallersResponse(new CallerSummaryResponse(query, target is null ? "Unknown" : "Exact", target, callers, await TargetSignatureAsync(target, cancellationToken)))), [], []);
    }

    private async Task<ProtocolResponse> HandleGetReferencesAsync(ProtocolRequest request, SessionState session, CancellationToken cancellationToken)
    {
        if (!TryGetQuery(request.Payload, out var query))
        {
            return Fail("ValidationError", "Reference requests must include a string 'query' field.");
        }

        var methodMatches = await _repositoryStore.FindMethodSummariesAsync(query, cancellationToken);
        var textBacked = await TryTextReferencesAsync(query, session, callersOnly: false, methodMatches, false, cancellationToken);
        if (textBacked is not null)
        {
            return textBacked;
        }

        var references = await _repositoryStore.FindReferencesAsync(query, cancellationToken);
        if (references.Count == 0)
        {
            // A suffix such as `Type.Member` finds nothing by name; resolve it the way `read` does and retry.
            var resolved = await ResolveCallTargetAsync(query, session, cancellationToken);
            if (resolved.Ambiguous is not null)
            {
                return resolved.Ambiguous;
            }

            if (resolved.Query != query)
            {
                methodMatches = await _repositoryStore.FindMethodSummariesAsync(resolved.Query, cancellationToken);
                references = await _repositoryStore.FindReferencesAsync(resolved.Query, cancellationToken);
            }
        }

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
        var unsupported = UnsupportedPythonCalleesResponse(query, methods);
        if (unsupported is not null)
        {
            return unsupported;
        }

        var resolved = await ResolveCallTargetAsync(query, session, cancellationToken);
        if (resolved.Ambiguous is not null)
        {
            return resolved.Ambiguous;
        }

        if (resolved.Overloads.Count > 1 && _repositoryStore is DevX.Infrastructure.Storage.SQLite.SqliteStorageInitializer sqlite)
        {
            var overloads = await OverloadSymbolsAsync(resolved, cancellationToken);
            var groups = CallOverloads.Callees(overloads, await sqlite.ListMethodCandidatesAsync(overloads[0].RepositoryId, cancellationToken));
            var distinct = groups.SelectMany(group => group.Callees).DistinctBy(callee => callee.CalleeMethodId).ToList();
            foreach (var callee in distinct)
            {
                await RecordHistoryAsync(() => _sessionStore.AddWorkingSetItemAsync(new AddWorkingSetItemRequest(session.Id, callee.CalleeMethodId), cancellationToken));
            }

            return new ProtocolResponse(true, JsonSerializer.SerializeToElement(new CalleesResponse(new CalleeSummaryResponse(query, "Exact", null, distinct, null, groups))), [], []);
        }

        if (resolved.Query != query)
        {
            methods = await _repositoryStore.FindMethodSummariesAsync(resolved.Query, cancellationToken);
        }

        var callees = await _repositoryStore.FindCalleesAsync(resolved.Query, cancellationToken);
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

        return new ProtocolResponse(true, JsonSerializer.SerializeToElement(new CalleesResponse(new CalleeSummaryResponse(query, target is null ? "Unknown" : "Exact", target, callees, await TargetSignatureAsync(target, cancellationToken)))), [], []);
    }

    private sealed record CallTarget(string Query, IReadOnlyList<SymbolSearchResult> Overloads, ProtocolResponse? Ambiguous);

    /// <summary>
    /// Resolves the target of callers/callees/references the way `read method` does (exact, module, suffix, bare name).
    /// Several definitions with one qualified name are overloads of one method; several qualified names are an
    /// ambiguity and answer with the same capped candidate list `read` gives. No candidate keeps the query as typed.
    /// </summary>
    private async Task<CallTarget> ResolveCallTargetAsync(string query, SessionState session, CancellationToken cancellationToken)
    {
        if (_repositoryStore is not ISymbolStore || await _repositoryStore.GetRepositoryRootPathAsync(session.RepositoryId, cancellationToken) is not { } root)
        {
            return new CallTarget(query, [], null);
        }

        var candidates = (await ReadableSymbolResolver.ResolveAsync(_repositoryStore, root, query, cancellationToken)).Candidates
            .Where(candidate => candidate.Kind is "Method" or "Constructor").DistinctBy(candidate => candidate.Id).ToList();
        if (candidates.Count == 0)
        {
            return new CallTarget(query, [], null);
        }

        if (candidates.Select(candidate => candidate.QualifiedName).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            var ranked = RankCandidates(candidates, query);
            return new CallTarget(query, candidates, new ProtocolResponse(false, null, [], [new ProtocolError("AmbiguousMatch",
                $"Symbol '{query}' matched multiple definitions ({candidates.Count}); pass a qualified name.",
                query, ranked.Select(s => s.QualifiedName).ToList(), ranked, candidates.Count)]));
        }

        return new CallTarget(candidates[0].QualifiedName, candidates, null);
    }

    private async Task<IReadOnlyList<Symbol>> OverloadSymbolsAsync(CallTarget target, CancellationToken cancellationToken)
    {
        var ids = target.Overloads.Select(candidate => candidate.Id).ToHashSet(StringComparer.Ordinal);
        return (await ((ISymbolStore)_repositoryStore).FindSymbolsByQualifiedNameAsync(target.Query, cancellationToken))
            .Where(symbol => ids.Contains(symbol.Id)).OrderBy(symbol => symbol.FilePath, StringComparer.Ordinal).ThenBy(symbol => symbol.StartLine).ToList();
    }

    private async Task<string?> TargetSignatureAsync(MethodSummary? target, CancellationToken cancellationToken)
    {
        if (target is null || _repositoryStore is not ISymbolStore symbols)
        {
            return null;
        }

        var symbol = (await symbols.FindSymbolsByQualifiedNameAsync(target.QualifiedName, cancellationToken)).FirstOrDefault(candidate => candidate.Id == target.Id);
        return symbol is null ? null : CallOverloads.SignatureOf(symbol);
    }

    private static ProtocolResponse? UnsupportedPythonCalleesResponse(string query, IReadOnlyList<MethodSummary> methods)
        => methods.Any(method => method.FilePath.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
            ? Fail("UnsupportedLanguage", $"Call graph queries are not supported for Python symbol '{query}'; use `read method {query}` and look at the body (callers/references are available, text-backed).")
            : null;

    /// <summary>
    /// `callers` / `references` for Python: resolve the target like `read` does, then search indexed file
    /// contents for the simple name as a whole word and attribute each hit to the innermost indexed symbol
    /// containing its line. Returns null when the target is not a Python symbol, so the symbol-table (C#)
    /// path runs unchanged.
    /// </summary>
    private async Task<ProtocolResponse?> TryTextReferencesAsync(string query, SessionState session, bool callersOnly, IReadOnlyList<MethodSummary> methodMatches, bool full, CancellationToken cancellationToken)
    {
        static bool IsPython(string? path) => path?.EndsWith(".py", StringComparison.OrdinalIgnoreCase) == true;
        IReadOnlyList<SymbolSearchResult> candidates = [];
        if (_repositoryStore is ISymbolStore)
        {
            var root = await _repositoryStore.GetRepositoryRootPathAsync(session.RepositoryId, cancellationToken);
            if (root is not null)
            {
                candidates = (await ReadableSymbolResolver.ResolveAsync(_repositoryStore, root, query, cancellationToken)).Candidates;
            }
        }
        if (!candidates.Any(candidate => IsPython(candidate.FilePath)) && !methodMatches.Any(method => IsPython(method.FilePath)))
        {
            return null;
        }
        if (candidates.Count == 0)
        {
            candidates = methodMatches.Where(method => IsPython(method.FilePath))
                .Select(method => new SymbolSearchResult(method.Id, "Method", method.Name, method.QualifiedName, method.FilePath, null, null)).ToList();
        }
        if (candidates.Count > 1)
        {
            var ranked = RankCandidates(candidates, query);
            return new ProtocolResponse(false, null, [], [new ProtocolError("AmbiguousMatch",
                $"Symbol '{query}' matched multiple definitions ({candidates.Count}); pass a qualified name.",
                query, ranked.Select(s => s.QualifiedName).ToList(), ranked, candidates.Count)]);
        }

        var target = candidates[0];
        var targetPath = target.FilePath ?? string.Empty;
        var targetLine = target.StartLine ?? 0;
        var name = target.Name.Length > 0 ? target.Name : target.QualifiedName.Split('.').Last();

        const int searchLimit = 5000;
        ContentSearchOutcome outcome;
        if (_repositoryStore is IContentSearchCoverageStore coverageStore)
        {
            outcome = await coverageStore.SearchFileContentsWithCoverageAsync($@"\b{Regex.Escape(name)}\b", searchLimit, cancellationToken);
        }
        else
        {
            var matches = await _repositoryStore.SearchFileContentsAsync($@"\b{Regex.Escape(name)}\b", searchLimit, cancellationToken);
            outcome = new ContentSearchOutcome(matches, new ContentSearchCoverage(matches.Count, searchLimit, null, null, false, false, "unknown", ["The configured store does not report search coverage."]));
        }

        var repositoryRoot = await _repositoryStore.GetRepositoryRootPathAsync(session.RepositoryId, cancellationToken);
        var lexed = new Dictionary<string, PythonLexicalRegions?>(StringComparer.Ordinal);
        PythonLexicalRegions? LexedSource(string path)
        {
            if (!IsPython(path) || repositoryRoot is null) return null;
            if (lexed.TryGetValue(path, out var cached)) return cached;
            PythonLexicalRegions? regions = null;
            try
            {
                var full = Path.GetFullPath(Path.Combine(repositoryRoot, path));
                if (full.StartsWith(Path.GetFullPath(repositoryRoot), StringComparison.Ordinal)) regions = PythonLexicalRegions.Scan(System.IO.File.ReadAllText(full));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
            lexed[path] = regions;
            return regions;
        }

        var hits = outcome.Matches
            .Where(match => !(match.Line == targetLine && string.Equals(match.Path, targetPath, StringComparison.Ordinal)))
            .Select(match => TextReferences.Classify(match, name, LexedSource(match.Path)))
            .OfType<TextReferences.Hit>()
            .Where(hit => !callersOnly || hit.Kind == "call")
            .ToList();
        var ranked2 = TextReferences.Rank(hits, targetPath);
        var kept = ranked2.Take(TextReferences.Cap).ToList();
        const int MaxGroupedSites = 2000;
        var grouped = callersOnly && !full && ranked2.Count > TextReferences.GroupThreshold;

        IReadOnlyDictionary<(string FileId, int Line), string> enclosing = new Dictionary<(string FileId, int Line), string>();
        if (_repositoryStore is DevX.Infrastructure.Storage.SQLite.SqliteStorageInitializer sqlite)
        {
            // Grouping needs the enclosing symbol of every call site, not only of the 50 that are listed.
            var enclosingScope = grouped ? ranked2.Take(MaxGroupedSites) : kept;
            enclosing = await sqlite.FindEnclosingSymbolsAsync(enclosingScope.Select(hit => (hit.FileId, hit.Line)).Distinct().ToList(), cancellationToken);
        }
        var groups = grouped
            ? ranked2.Take(MaxGroupedSites)
                .GroupBy(hit => (Enclosing: enclosing.GetValueOrDefault((hit.FileId, hit.Line), "<module>"), hit.Path))
                .Select(group => new TextReferenceGroup(group.Key.Enclosing, group.Count(), group.Key.Path, group.Select(hit => hit.Line).OrderBy(line => line).ToList()))
                .OrderByDescending(group => group.Count).ThenBy(group => group.Path, StringComparer.Ordinal).ThenBy(group => group.Enclosing, StringComparer.Ordinal)
                .ToList()
            : null;

        var references = kept.Select(hit => new TextReference(hit.Path, hit.Line, enclosing.GetValueOrDefault((hit.FileId, hit.Line), "<module>"), hit.Text, hit.Kind)).ToList();
        var response = new TextReferencesResponse(
            "text", query, target.QualifiedName, targetPath, targetLine,
            ranked2.Count, references.Count, ranked2.Count > references.Count,
            ranked2.Count(hit => hit.Kind == "call"), ranked2.Count(hit => hit.Kind == "usage"), ranked2.Count(hit => hit.Kind == "import"),
            outcome.Coverage, references,
            callersOnly ? new TextReferenceMetadata(TextReferences.CallersNote) : null,
            callersOnly ? grouped : null, groups);
        await RecordHistoryAsync(() => _sessionStore.MarkSymbolExpandedAsync(new MarkSymbolExpandedRequest(session.Id, target.Id), cancellationToken));
        return new ProtocolResponse(true, JsonSerializer.SerializeToElement(response), [], []);
    }
}
