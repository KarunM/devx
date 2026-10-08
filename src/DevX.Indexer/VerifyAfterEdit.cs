using System.Text.RegularExpressions;
using DevX.Domain;

namespace DevX.Indexer;

public sealed record VerifyAfterEditResult(ExecutionRecord Build, ExecutionRecord? Tests, bool BuildSkipped);

public sealed class VerifyAfterEdit
{
    public static async Task<ExecutionRecord> RunBuildAsync(DotnetBuildTestService service, string root, string file, CancellationToken token = default)
    {
        var extension = Path.GetExtension(file);
        if (extension.Equals(".py", StringComparison.OrdinalIgnoreCase)) return await service.RunPythonCompileAsync(root, file, token);
        if (!extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)) return SkippedBuild(file);
        var fullPath = Path.GetFullPath(Path.Combine(root, file));
        for (var current = new DirectoryInfo(Path.GetDirectoryName(fullPath)!); current is not null; current = current.Parent)
        {
            var project = current.EnumerateFiles("*.csproj").OrderBy(item => item.FullName, StringComparer.Ordinal).FirstOrDefault();
            if (project is not null) return await service.RunBuildAsync(root, Path.GetRelativePath(root, project.FullName), token);
        }
        return await service.RunBuildAsync(root, file, token);
    }

    public static bool HasBuildStep(string file) => Path.GetExtension(file).Equals(".cs", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(file).Equals(".py", StringComparison.OrdinalIgnoreCase);

    public static ExecutionRecord SkippedBuild(string file)
    {
        var extension = Path.GetExtension(file);
        return new ExecutionRecord("skipped", true, "", [], $"skipped (no build step for {extension})");
    }
    private readonly Func<string, Task<ExecutionRecord>> _build;
    private readonly Func<IReadOnlyList<string>, Task<ExecutionRecord>> _tests;
    private readonly Func<string, IReadOnlyList<string>> _relevantTestFiles;

    public VerifyAfterEdit(Func<string, Task<ExecutionRecord>> build, Func<IReadOnlyList<string>, Task<ExecutionRecord>> tests, Func<string, IReadOnlyList<string>> relevantTestFiles)
    {
        _build = build;
        _tests = tests;
        _relevantTestFiles = relevantTestFiles;
    }

    public async Task<VerifyAfterEditResult> RunAsync(string file)
    {
        var build = await _build(file);
        if (!build.Success) return new(build, null, false);
        var files = _relevantTestFiles(file);
        return new(build, await _tests(files), build.Summary.StartsWith("SKIP", StringComparison.Ordinal));
    }

    public static IReadOnlyList<string> FormatLines(string replaceMessage, string diff, VerifyAfterEditResult verification)
    {
        var lines = new List<string> { replaceMessage };
        if (!string.IsNullOrEmpty(diff)) lines.Add(diff);
        lines.Add("build: " + verification.Build.Summary);
        if (verification.Build.Success) lines.Add("tests: " + (verification.Tests?.Summary ?? "PASS tests — skipped"));
        else lines.Add("edit kept; fix forward or revert");
        return lines;
    }

    public static IReadOnlyList<string> FindRelevantTestFiles(string root, string changedFile)
    {
        var fullFile = Path.GetFullPath(Path.Combine(root, changedFile));
        if (!System.IO.File.Exists(fullFile)) return [];
        var source = System.IO.File.ReadAllText(fullFile);
        var names = Regex.Matches(source, @"\b(?:class|struct|interface|enum|def|function)\s+(\w+)")
            .Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();
        if (names.Length == 0) return [];
        var candidates = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && IsTestFile(root, path));
        return candidates.Where(path =>
        {
            var text = System.IO.File.ReadAllText(path);
            return names.Any(name => Regex.IsMatch(text, @"\b" + Regex.Escape(name) + @"\b"));
        }).Take(10).Select(path => Path.GetRelativePath(root, path)).ToArray();
    }

    public static IReadOnlyList<string> FindRelevantTestFiles(string root, string changedFile, ISymbolStore symbols, ISearchService search)
    {
        var fullFile = Path.GetFullPath(Path.Combine(root, changedFile));
        if (!System.IO.File.Exists(fullFile)) return [];
        var relativeChanged = Path.GetRelativePath(root, fullFile).Replace(Path.DirectorySeparatorChar, '/');
        var source = System.IO.File.ReadAllText(fullFile);
        var names = Regex.Matches(source, @"\b(?:class|struct|interface|enum|def|function)\s+(\w+)")
            .Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();
        var relevant = new List<string>();
        foreach (var name in names)
        {
            var indexedSymbols = symbols.FindSymbolsByNameAsync(name).GetAwaiter().GetResult();
            if (!indexedSymbols.Any(symbol => symbol.FilePath?.Replace(Path.DirectorySeparatorChar, '/') == relativeChanged)) continue;
            var matches = search.SearchTextAsync(new SearchTextRequest(name)).GetAwaiter().GetResult();
            foreach (var path in matches.ContentMatches?.Select(match => match.Path).Distinct(StringComparer.Ordinal) ?? Enumerable.Empty<string>())
            {
                if (!IsTestFile(root, Path.GetFullPath(Path.Combine(root, path)))) continue;
                if (!relevant.Contains(path, StringComparer.Ordinal)) relevant.Add(path);
                if (relevant.Count == 10) return relevant;
            }
        }
        return relevant;
    }

    private static bool IsTestFile(string root, string path)
    {
        var name = Path.GetFileName(path);
        var relative = Path.GetRelativePath(root, path);
        var testDirectory = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment is "test" or "tests" or "Tests");
        return testDirectory || name.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("Test.cs", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("test_", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("_test.py", StringComparison.OrdinalIgnoreCase);
    }
}
