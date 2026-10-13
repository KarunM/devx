using Microsoft.Data.Sqlite;
using DevX.Domain;
using DomainFile = DevX.Domain.File;
using DomainType = DevX.Domain.Type;
using System.Text.Json;
using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DevX.Infrastructure.Storage.SQLite;

public sealed partial class SqliteStorageInitializer
{
    public async Task<IReadOnlyList<FileSearchResult>> SearchFilesAsync(string query, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.id, f.path, p.name, r.name, r.root_path
            FROM files f
            INNER JOIN projects p ON p.id = f.project_id
            INNER JOIN repositories r ON r.id = p.repository_id
            WHERE lower(f.path) LIKE lower($query)
            ORDER BY CASE WHEN lower(f.path) = lower($exact) THEN 0 ELSE 1 END, f.path;
            """;
        command.Parameters.AddWithValue("$query", $"%{query}%");
        command.Parameters.AddWithValue("$exact", query);

        var results = new List<FileSearchResult>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new FileSearchResult(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                System.IO.File.Exists(Path.Combine(reader.GetString(4), reader.GetString(1))) ? null : "file missing"));
        }

        return results;
    }

    public async Task<IReadOnlyList<string>> GetIndexedFilePathsAsync(string root, CancellationToken token = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT f.path FROM files f JOIN projects p ON p.id = f.project_id JOIN repositories r ON r.id = p.repository_id WHERE r.root_path = $root ORDER BY f.path;";
        command.Parameters.AddWithValue("$root", Path.GetFullPath(root));
        var paths = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) paths.Add(reader.GetString(0));
        return paths;
    }

    public async Task<IReadOnlyList<ContentSearchResult>> SearchFileContentsAsync(
        string query,
        int limit = 100,
        CancellationToken cancellationToken = default)
        => (await SearchFileContentsWithCoverageAsync(query, limit, cancellationToken)).Matches;

    public async Task<ContentSearchOutcome> SearchFileContentsWithCoverageAsync(
        string query,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
        {
            return new ContentSearchOutcome([], new ContentSearchCoverage(0, Math.Max(0, limit), null, null, false, false, "none", ["No scan was performed for an empty query or non-positive result limit."]));
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.id, f.path, r.root_path
            FROM files f
            INNER JOIN projects p ON p.id = f.project_id
            INNER JOIN repositories r ON r.id = p.repository_id
            ORDER BY f.path;
            """;

        var indexedFiles = new List<(string Id, string Path, string Root)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                indexedFiles.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        // SQLite is an inventory of candidates, not authority to follow a path that has
        // become a link since indexing. Validate every row against its own repository root
        // before either ripgrep or the in-process fallback can read it.
        var safeIndexedFiles = indexedFiles
            .Where(file => RepositoryPathGuard.TryResolve(file.Root, file.Path, out _, out _))
            .ToList();
        var warningCount = indexedFiles.Count - safeIndexedFiles.Count;

        // ripgrep -- the repository's configured copy, the one bundled with DevX, or the one on
        // PATH, in that order: it is the regex engine, and it reads the tree instead of
        // every indexed file, which is what makes `a|b` and a regex possible at all. The
        // in-process scan below stays as the fallback and its result set is the same shape, so
        // nothing downstream -- and no `--format json` payload -- depends on which one answered.
        var ripgrepHits = new List<(string Root, RipgrepContentSearcher.Hit Hit)>();
        var ripgrepAnswered = safeIndexedFiles.Count > 0;
        var scanCapped = false;
        foreach (var root in safeIndexedFiles.Select(file => Path.GetFullPath(file.Root)).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
        {
            if (!RipgrepContentSearcher.TrySearch(root, query, out var found, out var rootScanCapped, cancellationToken))
            {
                ripgrepAnswered = false;
                break;
            }

            scanCapped |= rootScanCapped;
            ripgrepHits.AddRange(found.Select(hit => (root, hit)));
        }

        if (ripgrepAnswered)
        {
            // Only files the index knows: a ContentSearchResult carries a FileId, and the
            // in-process scan could only ever match indexed files either.
            var indexedByIdentity = safeIndexedFiles.ToDictionary(
                candidate => (Root: Path.GetFullPath(candidate.Root), Path: NormalizeContentPath(candidate.Path)),
                candidate => candidate,
                new IndexedFileIdentityComparer());
            var results = new List<(string Path, int Line, ContentSearchResult Result)>();
            var unindexedHits = 0;
            foreach (var (root, hit) in ripgrepHits)
            {
                var hitPath = NormalizeContentPath(hit.Path);
                if (!RepositoryPathGuard.TryResolve(root, hitPath, out _, out _))
                {
                    warningCount++;
                    continue;
                }
                if (!indexedByIdentity.TryGetValue((root, hitPath), out var file))
                {
                    // A file ripgrep can read but the index never covered (docs, config) is not an
                    // unsafe indexed path: drop it and count it where JSON consumers can see it.
                    unindexedHits++;
                    continue;
                }
                AddBoundedResult(results, (hitPath, hit.Line, new ContentSearchResult(file.Id, hitPath, hit.Line, MatchColumn(hit.Text, query), hit.Text)), limit + 1);
            }
            var outputTruncated = results.Count > limit;
            var returned = results.Take(limit).Select(item => item.Result).ToList();
            var warnings = new List<string> { "Ripgrep honors ignore and hidden-file rules, so indexed-path coverage is unknown." };
            if (scanCapped) warnings.Add("Ripgrep stopped at its 5,000-match scan ceiling for at least one root.");
            if (warningCount > 0) warnings.Add($"{warningCount} indexed path(s) could not be searched safely.");
            bool? complete = outputTruncated ? false : null;
            bool? reportedTruncation = outputTruncated ? true : null;
            return new ContentSearchOutcome(returned, new ContentSearchCoverage(returned.Count, limit, reportedTruncation, complete, scanCapped, false, "ripgrep", warnings, unindexedHits));
        }

        // The fallback must not be a worse tool than the engine it stands in for, or the answer
        // would depend on whether ripgrep happens to be installed. So the pattern is a regex
        // here too; only one that will not compile -- `GetValueOrDefault(`, which agents really
        // typed -- is matched as literal alternatives instead.
        System.Text.RegularExpressions.Regex? pattern = null;
        var alternatives = new[] { query };
        try
        {
            pattern = new System.Text.RegularExpressions.Regex(
                query,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        }
        catch (ArgumentException)
        {
            // Not a regex: `a|b` is still the alternation an agent expects, so split on it.
            var parts = query.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length > 0)
            {
                alternatives = parts;
            }
        }

        var fallbackResults = new List<(string Path, int Line, ContentSearchResult Result)>();
        var fallbackWarnings = warningCount;
        foreach (var file in safeIndexedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!RepositoryPathGuard.TryResolve(file.Root, file.Path, out var fullPath, out _))
            {
                fallbackWarnings++;
                continue;
            }
            if (!System.IO.File.Exists(fullPath))
            {
                fallbackWarnings++;
                continue;
            }

            string[] lines;
            try
            {
                lines = await System.IO.File.ReadAllLinesAsync(fullPath, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                fallbackWarnings++;
                continue;
            }

            for (var index = 0; index < lines.Length; index++)
            {
                var column = -1;
                if (pattern is not null)
                {
                    var match = pattern.Match(lines[index]);
                    if (match.Success)
                    {
                        column = match.Index;
                    }
                }
                else
                {
                    foreach (var alternative in alternatives)
                    {
                        var at = lines[index].IndexOf(alternative, StringComparison.OrdinalIgnoreCase);
                        if (at >= 0 && (column < 0 || at < column))
                        {
                            column = at;
                        }
                    }
                }

                if (column < 0)
                {
                    continue;
                }

                AddBoundedResult(fallbackResults, (file.Path, index + 1, new ContentSearchResult(file.Id, file.Path, index + 1, column + 1, lines[index].Trim())), limit + 1);
            }
        }

        var fallbackTruncated = fallbackResults.Count > limit;
        var fallbackReturned = fallbackResults.Take(limit).Select(item => item.Result).ToList();
        var fallbackMessages = fallbackWarnings == 0 ? Array.Empty<string>() : new[] { $"{fallbackWarnings} indexed file(s) could not be searched completely." };
        bool? fallbackComplete = fallbackTruncated ? false : fallbackWarnings > 0 ? (bool?)null : true;
        bool? fallbackOutputTruncated = fallbackTruncated ? true : fallbackWarnings > 0 ? null : false;
        return new ContentSearchOutcome(fallbackReturned, new ContentSearchCoverage(fallbackReturned.Count, limit, fallbackOutputTruncated, fallbackComplete, false, true, "fallback", fallbackMessages));
    }

    private static void AddBoundedResult(List<(string Path, int Line, ContentSearchResult Result)> results,
        (string Path, int Line, ContentSearchResult Result) result, int capacity)
    {
        var index = results.BinarySearch(result, ContentSearchResultOrderComparer.Instance);
        if (index < 0) index = ~index;
        results.Insert(index, result);
        if (results.Count > capacity) results.RemoveAt(results.Count - 1);
    }

    private static string NormalizeContentPath(string path)
        => OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path;

    private sealed class ContentSearchResultOrderComparer : IComparer<(string Path, int Line, ContentSearchResult Result)>
    {
        public static readonly ContentSearchResultOrderComparer Instance = new();
        public int Compare((string Path, int Line, ContentSearchResult Result) x, (string Path, int Line, ContentSearchResult Result) y)
        {
            var path = StringComparer.Ordinal.Compare(x.Path, y.Path);
            if (path != 0) return path;
            var line = x.Line.CompareTo(y.Line);
            return line != 0 ? line : StringComparer.Ordinal.Compare(x.Result.FileId, y.Result.FileId);
        }
    }

    private sealed class IndexedFileIdentityComparer : IEqualityComparer<(string Root, string Path)>
    {
        private readonly StringComparer _pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        public bool Equals((string Root, string Path) x, (string Root, string Path) y)
            => _pathComparer.Equals(x.Root, y.Root) && _pathComparer.Equals(x.Path, y.Path);
        public int GetHashCode((string Root, string Path) value)
            => HashCode.Combine(_pathComparer.GetHashCode(value.Root), _pathComparer.GetHashCode(value.Path));
    }

    /// <summary>The 1-based column of a match, for the <c>Column</c> field of a content result.
    /// The query is a regex whenever ripgrep answered, so the literal is tried first -- a plain
    /// identifier query is the common case and this keeps its column identical to the
    /// in-process scan's -- then the regex, then 1.</summary>
    private static int MatchColumn(string text, string query)
    {
        var literal = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (literal >= 0)
        {
            return literal + 1;
        }

        try
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                text, query, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match.Index + 1;
            }
        }
        catch (System.Text.RegularExpressions.RegexParseException)
        {
            // Not a regex: the pattern was a fixed string ripgrep matched, so 1 is as good as
            // any and nothing here may throw over a column number.
        }

        return 1;
    }
}
