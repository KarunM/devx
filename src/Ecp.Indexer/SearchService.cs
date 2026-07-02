using Ecp.Domain;

namespace Ecp.Indexer;

public sealed class SearchService : ISearchService
{
    private readonly IRepositoryStore _repositoryStore;
    private readonly ISymbolStore _symbolStore;

    public SearchService(IRepositoryStore repositoryStore, ISymbolStore symbolStore)
    {
        _repositoryStore = repositoryStore;
        _symbolStore = symbolStore;
    }

    public async Task<SearchResult> SearchSymbolAsync(SearchSymbolRequest request, CancellationToken cancellationToken = default)
    {
        var exact = await _symbolStore.FindSymbolsByNameAsync(request.Query, cancellationToken);
        var qualifiedExact = await _symbolStore.FindSymbolsByQualifiedNameAsync(request.Query, cancellationToken);
        var partial = await _symbolStore.FindSymbolsContainingAsync(request.Query, cancellationToken);
        var files = await _repositoryStore.SearchFilesAsync(request.Query, cancellationToken);

        var exactResults = exact
            .Concat(qualifiedExact)
            .DistinctBy(symbol => symbol.Id)
            .Select(symbol => MapSymbol(symbol, 0))
            .OrderBy(result => result.QualifiedName, StringComparer.Ordinal)
            .ToList();

        var partialResults = partial
            .Where(symbol => !exact.Any(x => x.Id == symbol.Id) && !qualifiedExact.Any(x => x.Id == symbol.Id))
            .Select(symbol => MapSymbol(symbol, 1))
            .OrderBy(result => result.QualifiedName, StringComparer.Ordinal)
            .ToList();

        return new SearchResult(
            exactResults.Concat(partialResults).ToList(),
            files.OrderBy(file => file.Path, StringComparer.Ordinal).ToList());
    }

    public async Task<SearchResult> SearchTextAsync(SearchTextRequest request, CancellationToken cancellationToken = default)
    {
        var symbols = await _symbolStore.FindSymbolsContainingAsync(request.Query, cancellationToken);
        var files = await _repositoryStore.SearchFilesAsync(request.Query, cancellationToken);

        return new SearchResult(
            symbols.Select(symbol => MapSymbol(symbol, 0)).OrderBy(result => result.QualifiedName, StringComparer.Ordinal).ToList(),
            files.OrderBy(file => file.Path, StringComparer.Ordinal).ToList());
    }

    private static SymbolSearchResult MapSymbol(Symbol symbol, int _)
        => new(symbol.Id, symbol.Kind, symbol.Name, symbol.QualifiedName, null);
}
