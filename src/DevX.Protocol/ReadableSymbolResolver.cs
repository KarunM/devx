using DevX.Domain;
using DevX.Indexer;

namespace DevX.Protocol;

public sealed record ReadableResolution(IReadOnlyList<SymbolSearchResult> Candidates, string ResolvedVia);

public static class ReadableSymbolResolver
{
    public static async Task<ReadableResolution> ResolveAsync(IRepositoryStore store, string root, string query, CancellationToken token)
    {
        var terminal = query.Split('.').Last();
        var search = new SearchService(store, (ISymbolStore)store);
        var candidates = (await search.SearchSymbolAsync(new SearchSymbolRequest(terminal), token)).Symbols;
        if (Guid.TryParse(query, out _))
            candidates = (await search.SearchSymbolAsync(new SearchSymbolRequest(""), token)).Symbols;
        // Search stores may contain several workspaces. Restrict candidates before resolving names.
        var rootRepository = store as DevX.Infrastructure.Storage.SQLite.SqliteStorageInitializer;
        if (rootRepository is not null)
        {
            var repository = await rootRepository.GetRepositoryByRootPathAsync(root, token);
            if (repository is not null)
            {
                var ids = (await ((ISymbolStore)store).FindSymbolsContainingAsync(Guid.TryParse(query, out _) ? "" : terminal, token))
                    .Where(s => s.RepositoryId == repository.Id.Value.ToString()).Select(s => s.Id).ToHashSet();
                candidates = candidates.Where(s => ids.Contains(s.Id)).ToList();
            }
        }
        var exact = candidates.Where(s => Same(s.Id, query) || Same(s.QualifiedName, query)).ToList();
        if (exact.Count > 0) return new(exact, "exact");
        var ancestors = new HashSet<string>(StringComparer.Ordinal);
        for (var folder = new DirectoryInfo(root); folder is not null; folder = folder.Parent) ancestors.Add(folder.Name);
        var segments = query.Split('.');
        var stripped = 0;
        while (stripped < segments.Length - 1 && ancestors.Contains(segments[stripped])) stripped++;
        var normalized = string.Join('.', segments.Skip(stripped));
        exact = candidates.Where(s => Same(s.QualifiedName, normalized)).ToList();
        if (exact.Count > 0) return new(exact, stripped > 0 ? "prefix-stripped" : "exact");
        var module = candidates.Where(s => ModuleMatch(s, normalized)).ToList();
        if (module.Count > 0) return new(module, stripped > 0 ? "prefix-stripped" : "module-path");
        // A supplied module prefix is a constraint; never silently return another module's symbol.
        if (normalized.Contains('.')) return new([], stripped > 0 ? "prefix-stripped" : "module-path");
        return new(candidates.Where(s => Same(s.Name, normalized)).ToList(), "short-name");
    }

    private static bool ModuleMatch(SymbolSearchResult symbol, string query)
    {
        if (symbol.FilePath is null) return false;
        var module = Path.ChangeExtension(symbol.FilePath, null).Replace('\\', '.').Replace('/', '.');
        var qualified = symbol.QualifiedName.Replace(".<module>", "", StringComparison.Ordinal);
        if (Same(qualified, query)) return true;
        var suffix = "." + symbol.Name;
        if (Same(query, module + suffix)) return true;
        var parts = symbol.QualifiedName.Split('.');
        var container = parts.Length > 1 ? parts[^2] : "";
        return Same(query, module + "." + container + suffix)
            || (container != "<module>" && Same(query, container + suffix));
    }
    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
