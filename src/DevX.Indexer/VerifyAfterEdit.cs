using System.Text.RegularExpressions;
using DevX.Domain;

namespace DevX.Indexer;

public sealed record VerifyAfterEditResult(ExecutionRecord Build, ExecutionRecord? Tests, bool BuildSkipped);

/// <summary>What a verification runs. A bare `--verify`, and `devx verify` with no mode, is Both.</summary>
public enum VerifyMode
{
    Build,
    Test,
    Both,
}

public sealed class VerifyAfterEdit
{
    /// <summary>`build`, `test` (or `tests`), `both` -- anything else is the fallback, so an
    /// argument someone mistyped runs the safe default instead of failing the edit.</summary>
    public static VerifyMode ParseMode(string? value, VerifyMode fallback = VerifyMode.Both) => value?.Trim().ToLowerInvariant() switch
    {
        "build" => VerifyMode.Build,
        "test" or "tests" => VerifyMode.Test,
        "both" => VerifyMode.Both,
        _ => fallback,
    };

    public static string ModeName(VerifyMode mode) => mode switch
    {
        VerifyMode.Build => "build",
        VerifyMode.Test => "test",
        _ => "both",
    };

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
        => await RunAsync(file, VerifyMode.Both);

    /// <summary>
    /// `--verify=test` runs the relevant tests and no build, so a Python repository that has
    /// no compile step to speak of does not pay for one; the build is then reported as skipped
    /// rather than claimed.
    /// </summary>
    public async Task<VerifyAfterEditResult> RunAsync(string file, VerifyMode mode)
    {
        if (mode == VerifyMode.Test)
        {
            return new(TestOnlyBuild(file), await _tests(_relevantTestFiles(file)), true);
        }

        var build = await _build(file);
        if (!build.Success) return new(build, null, false);
        if (mode == VerifyMode.Build) return new(build, null, false);
        var files = _relevantTestFiles(file);
        return new(build, await _tests(files), build.Summary.StartsWith("skip", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The stand-in build record for `--verify=test`: success, and named as skipped so
    /// nothing downstream reads it as a build that ran.</summary>
    public static ExecutionRecord TestOnlyBuild(string file)
        => new("build", true, string.Empty, [], "skipped (--verify=test)");

    /// <summary>`--verify`, `--verify=build|test|both`, `--no-verify`, or no flag at all. Null
    /// means the caller did not ask to verify, so an edit stays exactly as it was in 1.6.13
    /// (brief OSS14 adjustment, 2026-10-09: verifying is opt-in on every edit). The last flag
    /// on the line wins, so `--verify=build --no-verify` does not verify.</summary>
    public static VerifyMode? ParseVerifyFlag(IReadOnlyList<string> args)
    {
        VerifyMode? mode = null;
        foreach (var arg in args)
        {
            if (arg.Equals("--no-verify", StringComparison.Ordinal)) mode = null;
            else if (arg.Equals("--verify", StringComparison.Ordinal)) mode = VerifyMode.Both;
            else if (arg.StartsWith("--verify=", StringComparison.Ordinal)) mode = ParseMode(arg["--verify=".Length..]);
        }
        return mode;
    }

    /// <summary>True for the words `devx verify` accepts as its mode argument.</summary>
    public static bool IsModeName(string? value)
        => value?.Trim().ToLowerInvariant() is "build" or "test" or "tests" or "both";

    private const string LogMarker = " log: ";

    /// <summary>The log path is already inside each summary, so the one-line answer prints it once.</summary>
    public static string StripLog(string summary)
    {
        var marker = summary.LastIndexOf(LogMarker, StringComparison.Ordinal);
        return marker < 0 ? summary : summary[..marker];
    }

    /// <summary>Where the full log is, taken from whichever record wrote one.</summary>
    public static string? LogPath(VerifyAfterEditResult verification)
    {
        foreach (var summary in new[] { verification.Tests?.Summary, verification.Build.Summary })
        {
            if (summary is null) continue;
            var marker = summary.LastIndexOf(LogMarker, StringComparison.Ordinal);
            if (marker >= 0) return summary[(marker + LogMarker.Length)..].Trim();
        }
        return null;
    }

    /// <summary>A successful verification answers in ONE line -- status, what ran, and where the
    /// full log is (owner 2026-10-09: verifying after an edit must not cost five lines of reply).
    /// A failure goes through <see cref="FormatVerifyLines"/> instead, which keeps the
    /// `path:line: message` lines.</summary>
    public static string FormatOneLine(string label, VerifyAfterEditResult verification)
    {
        var parts = new List<string>();
        if (!verification.BuildSkipped) parts.Add(StripLog(verification.Build.Summary));
        if (verification.Tests is not null) parts.Add(StripLog(verification.Tests.Summary));
        if (parts.Count == 0) parts.Add(StripLog(verification.Build.Summary));
        var log = LogPath(verification);
        var line = $"PASS {label} — {string.Join("; ", parts)}";
        return log is null ? line : $"{line} — log {log}";
    }

    /// <summary>The multi-line failure answer: each run's own summary, which already carries the
    /// first errors as `path:line: message`, and an explicit note for whatever did not run.</summary>
    public static IReadOnlyList<string> FormatVerifyLines(VerifyAfterEditResult verification)
    {
        var lines = new List<string>();
        if (verification.BuildSkipped) lines.Add("build: skipped (--verify=test)");
        else lines.AddRange(Split(verification.Build.Summary));
        if (verification.Build.Success && verification.Tests is { } tests) lines.AddRange(Split(tests.Summary));
        else if (!verification.Build.Success) lines.Add("tests: skipped (build failed)");
        return lines.Where(line => line.Length > 0).ToArray();
    }

    private static IEnumerable<string> Split(string summary)
        => summary.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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

    public static bool IsTestFile(string root, string path)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith("test_", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("_test.py", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("Test.cs", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        // A support module under a test directory is not a test. pytest on `tests/conftest.py`
        // collects nothing and exits 5, which turned every verify on the click repository into a
        // false failure -- measured 2026-10-09, when `devx replace --verify` on src/click/core.py
        // reported "FAIL tests — 0 failed of 0" over five passing files and one conftest.
        if (name.StartsWith("conftest", StringComparison.OrdinalIgnoreCase)
            || name.Equals("__init__.py", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var relative = Path.GetRelativePath(root, path);
        return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment is "test" or "tests" or "Tests");
    }
}
