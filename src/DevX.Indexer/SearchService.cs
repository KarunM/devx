using DevX.Domain;

namespace DevX.Indexer;

public sealed class SearchService : ISearchService
{
    private readonly IRepositoryStore _repositoryStore;
    private readonly ISymbolStore _symbolStore;
    private readonly ILanguageParser _languageParser;

    public SearchService(IRepositoryStore repositoryStore, ISymbolStore symbolStore, ILanguageParser? languageParser = null)
    {
        _repositoryStore = repositoryStore;
        _symbolStore = symbolStore;
        _languageParser = languageParser ?? new CompositeLanguageParser();
    }

    public async Task<SearchResult> SearchSymbolAsync(SearchSymbolRequest request, CancellationToken cancellationToken = default)
    {
        var exact = await _symbolStore.FindSymbolsByNameAsync(request.Query, cancellationToken);
        var qualifiedExact = await _symbolStore.FindSymbolsByQualifiedNameAsync(request.Query, cancellationToken);
        var partial = await _symbolStore.FindSymbolsContainingAsync(request.Query, cancellationToken);
        var files = await _repositoryStore.SearchFilesAsync(request.Query, cancellationToken);
        var hydrated = await HydrateMissingLocationsAsync(
            exact.Concat(qualifiedExact).Concat(partial).DistinctBy(symbol => symbol.Id).ToList(),
            cancellationToken);
        var statuses = new Dictionary<string, string?>();
        foreach (var symbol in hydrated)
        {
            var root = await _repositoryStore.GetRepositoryRootPathAsync(new RepositoryId(Guid.Parse(symbol.RepositoryId)), cancellationToken);
            if (root is null || symbol.FilePath is null)
            {
                statuses[symbol.Id] = null;
            }
            else if (!RepositoryPathGuard.TryResolve(root, symbol.FilePath, out var safePath, out _))
            {
                statuses[symbol.Id] = "unsafe path";
            }
            else
            {
                statuses[symbol.Id] = !System.IO.File.Exists(safePath) ? "file missing" : null;
            }
        }
        var hydratedById = hydrated.ToDictionary(symbol => symbol.Id, StringComparer.Ordinal);

        var exactResults = exact
            .Concat(qualifiedExact)
            .DistinctBy(symbol => symbol.Id)
            .Select(symbol => hydratedById.GetValueOrDefault(symbol.Id, symbol))
            .Select(symbol => MapSymbol(symbol, 0))
            .Select(symbol => symbol with { FileStatus = statuses.GetValueOrDefault(symbol.Id) })
            .OrderBy(result => result.QualifiedName, StringComparer.Ordinal)
            .ToList();

        var partialResults = partial
            .Where(symbol => !exact.Any(x => x.Id == symbol.Id) && !qualifiedExact.Any(x => x.Id == symbol.Id))
            .Select(symbol => hydratedById.GetValueOrDefault(symbol.Id, symbol))
            .Select(symbol => MapSymbol(symbol, 1))
            .Select(symbol => symbol with { FileStatus = statuses.GetValueOrDefault(symbol.Id) })
            .OrderBy(result => result.QualifiedName, StringComparer.Ordinal)
            .ToList();

        return new SearchResult(
            exactResults.Concat(partialResults).ToList(),
            files.OrderBy(file => file.Path, StringComparer.Ordinal).ToList());
    }

    public async Task<SearchResult> SearchTextAsync(SearchTextRequest request, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ContentSearchResult> contentMatches;
        ContentSearchCoverage coverage;
        if (_repositoryStore is IContentSearchCoverageStore coverageStore)
        {
            var outcome = await coverageStore.SearchFileContentsWithCoverageAsync(request.Query, cancellationToken: cancellationToken);
            contentMatches = outcome.Matches;
            coverage = outcome.Coverage;
        }
        else
        {
            contentMatches = await _repositoryStore.SearchFileContentsAsync(request.Query, cancellationToken: cancellationToken);
            coverage = new ContentSearchCoverage(contentMatches.Count, 100, null, null, false, false, "unknown", ["The configured store does not report search coverage."]);
        }
        var files = await _repositoryStore.SearchFilesAsync(request.Query, cancellationToken);

        return new SearchResult(
            [],
            files.OrderBy(file => file.Path, StringComparer.Ordinal).ToList(),
            contentMatches,
            coverage);
    }

    private static SymbolSearchResult MapSymbol(Symbol symbol, int _)
        => new(symbol.Id, symbol.Kind, symbol.Name, symbol.QualifiedName, symbol.FilePath, symbol.StartLine, symbol.EndLine);

    private async Task<IReadOnlyList<Symbol>> HydrateMissingLocationsAsync(
        IReadOnlyList<Symbol> symbols,
        CancellationToken cancellationToken)
    {
        var roots = new Dictionary<string, string?>(StringComparer.Ordinal);
        var parsedFiles = new Dictionary<string, ParsedFile?>(StringComparer.OrdinalIgnoreCase);
        var results = new List<Symbol>(symbols.Count);

        foreach (var symbol in symbols)
        {
            if (symbol.StartLine is not null && symbol.EndLine is not null)
            {
                results.Add(symbol);
                continue;
            }

            if (string.IsNullOrWhiteSpace(symbol.FilePath) || !Guid.TryParse(symbol.RepositoryId, out var repositoryGuid))
            {
                results.Add(symbol);
                continue;
            }

            if (!roots.TryGetValue(symbol.RepositoryId, out var root))
            {
                root = await _repositoryStore.GetRepositoryRootPathAsync(new RepositoryId(repositoryGuid), cancellationToken);
                roots[symbol.RepositoryId] = root;
            }

            if (string.IsNullOrWhiteSpace(root))
            {
                results.Add(symbol);
                continue;
            }

            var absolutePath = Path.GetFullPath(Path.Combine(root, symbol.FilePath));
            if (!parsedFiles.TryGetValue(absolutePath, out var parsed))
            {
                try
                {
                    parsed = System.IO.File.Exists(absolutePath) && _languageParser.CanParse(absolutePath)
                        ? _languageParser.Parse(absolutePath, await System.IO.File.ReadAllTextAsync(absolutePath, cancellationToken))
                        : null;
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    parsed = null;
                }
                parsedFiles[absolutePath] = parsed;
            }

            var location = parsed is null ? null : FindLocation(parsed, symbol);
            if (location is null)
            {
                results.Add(symbol);
                continue;
            }

            var hydrated = symbol with { StartLine = location.Value.StartLine, EndLine = location.Value.EndLine };
            await _symbolStore.UpsertSymbolAsync(hydrated, cancellationToken);
            results.Add(hydrated);
        }

        return results;
    }

    private static (int StartLine, int EndLine)? FindLocation(ParsedFile parsed, Symbol symbol)
    {
        var preferredTypes = parsed.Types
            .OrderByDescending(type => symbol.QualifiedName.EndsWith($".{type.Name}.{symbol.Name}", StringComparison.Ordinal))
            .ToList();

        if (symbol.Kind is "Class" or "Struct" or "Interface" or "Type" or "Document")
        {
            var type = preferredTypes.FirstOrDefault(candidate => candidate.Name == symbol.Name);
            return type is null ? null : (type.StartLine, type.EndLine);
        }

        if (symbol.Kind is "Property")
        {
            var property = preferredTypes.SelectMany(type => type.Properties).FirstOrDefault(candidate => candidate.Name == symbol.Name);
            return property is null ? null : (property.StartLine, property.EndLine);
        }

        if (symbol.Kind is "Field")
        {
            var field = preferredTypes.SelectMany(type => type.Fields).FirstOrDefault(candidate => candidate.Name == symbol.Name);
            return field is null ? null : (field.StartLine, field.EndLine);
        }

        var methods = preferredTypes.SelectMany(type => type.Methods).Where(candidate => candidate.Name == symbol.Name).ToList();
        var method = !string.IsNullOrWhiteSpace(symbol.Signature)
            ? methods.FirstOrDefault(candidate => candidate.Signature == symbol.Signature) ?? methods.FirstOrDefault()
            : methods.FirstOrDefault();
        return method is null ? null : (method.StartLine, method.EndLine);
    }
}
