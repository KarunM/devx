using System.Diagnostics;
using System.Collections;
using System.Text.Json;
using System.Text.RegularExpressions;
using DevX.Domain;
using File = System.IO.File;

namespace DevX.Indexer;

public sealed class DotnetBuildTestService
{
    public Task<ExecutionRecord> RunBuildAsync(string root, CancellationToken token = default) => RunBuildAsync(root, null, token);
    // The repository's own answer, when it has one, beats DevX's guess about how it is built.
    public Task<ExecutionRecord> RunBuildAsync(string root, string? selector, CancellationToken token = default)
        => DevXCommandConfig.TryRead(root, "build") is { } configured
            ? RunConfiguredAsync(root, configured, selector, Timeout.InfiniteTimeSpan, token, "build")
            : IsDotnetProject(root, selector) ? RunAsync("dotnet", GetBuildArguments(root, selector), root, token) : RunPythonBuildAsync(root, selector, token);
    public Task<ExecutionRecord> RunPythonCompileAsync(string root, string file, CancellationToken token = default)
        => RunPythonBuildAsync(root, file, token);
    public Task<ExecutionRecord> RunTestsAsync(string root, CancellationToken token = default) => RunTestsAsync(root, null, token);
    public Task<ExecutionRecord> RunTestsAsync(string root, string? selector, CancellationToken token = default)
        => DevXCommandConfig.TryRead(root, "test") is { } configured
            ? RunConfiguredAsync(root, configured, selector, Timeout.InfiniteTimeSpan, token, "test")
            : IsDotnetProject(root, selector) ? RunAsync("dotnet", GetTestArguments(root, selector), root, token, "test") : RunPythonTestsAsync(root, selector, token);

    public async Task<ExecutionRecord> RunTestsAsync(
        string root,
        IReadOnlyList<string> selectors,
        TimeSpan timeout,
        CancellationToken token = default,
        string? filter = null)
    {
        if (selectors.Count == 0)
        {
            if (DevXCommandConfig.TryRead(root, "test") is { } configured)
            {
                return await RunConfiguredAsync(root, configured, null, timeout, token, "test");
            }

            // Django's suite is not a pytest suite. It is `tests/runtests.py`, optionally with
            // a dotted label, and running pytest at the root of a Django checkout collects
            // nothing and reports it as a pass -- which is how the LG1 agent ended up with
            // "0 failed of 0" and then installing pytest to make the tool agree with it.
            if (DjangoTestScript(root) is { } runtests)
            {
                return await RunWithTimeoutAsync(PythonExecutable(root), $"\"{runtests}\"", root, timeout, token, "test");
            }

            return await RunWithTimeoutAsync(
                IsDotnetProject(root, null) ? "dotnet" : PythonExecutable(root),
                IsDotnetProject(root, null) ? AddTestFilter(GetTestArguments(root, null), filter) : "-m pytest",
                root, timeout, token, "test");
        }

        var executions = new List<ExecutionRecord>();
        var timer = Stopwatch.StartNew();
        foreach (var selector in selectors)
        {
            var remaining = timeout - timer.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                executions.Add(TimeoutRecord(selector, root, timeout));
                break;
            }

            var path = NormalizeSelector(root, selector);
            // A selector that names something on disk is a path; anything else is a runner
            // label -- `queries.tests.QueriesTests` for Django, a class name for pytest. A
            // label is only meaningful to the runner that understands it, so it is handed to
            // runtests.py rather than to `dotnet test`.
            var label = File.Exists(path) || Directory.Exists(path) ? null : selector;
            // The repository's own answer wins for EVERY target: a path, a label, or nothing.
            // Consulting it only for a label meant the config the benchmark hands arm B was
            // inert for `devx test tests/test_utils.py` -- the run fell through to discovery
            // and the configured runner was never used, which is the defect 1.6.10 fixes.
            if (DevXCommandConfig.TryRead(root, "test") is { } configured)
            {
                executions.Add(await RunConfiguredAsync(root, configured, selector, remaining, token, "test"));
            }
            else if (selectors.Count == 1 && label is not null && DjangoTestScript(root) is { } runtests)
            {
                executions.Add(await RunWithTimeoutAsync(PythonExecutable(root), $"\"{runtests}\" \"{label}\"", root, remaining, token, "test"));
            }
            else if (path.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
            {
                if (IsStandaloneCheckScript(root, path))
                {
                    var command = BuildPythonScriptTestCommand(root, path);
                    executions.Add(await RunWithTimeoutAsync(command.FileName, command.Arguments, root, remaining, token, "check"));
                }
                else if (selectors.Count == 1 && LooksLikePytestTestFile(path))
                {
                    var command = BuildPythonFileTestCommand(root, path);
                    executions.Add(await RunWithTimeoutAsync(command.FileName, command.Arguments, root, remaining, token, "test"));
                }
                else
                {
                    var pytest = LooksLikePytest(path);
                    var arguments = pytest ? $"-m pytest \"{path}\"" : $"\"{path}\"";
                    executions.Add(await RunWithTimeoutAsync(PythonExecutable(root), arguments, root, remaining, token, pytest ? "test" : "check"));
                }
            }
            else if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                var command = BuildCSharpFileTestCommand(root, path);
                executions.Add(await RunWithTimeoutAsync(command.FileName, AddTestFilter(command.Arguments, filter), root, remaining, token, "test"));
            }
            else if (Directory.Exists(path) && ContainsPytestTestFiles(path))
            {
                executions.Add(await RunWithTimeoutAsync(PythonExecutable(root), $"-m pytest \"{path}\"", root, remaining, token, "test"));
            }
            else if (Directory.Exists(path) && BuildCSharpDirectoryTestCommand(root, path) is { } directoryCommand)
            {
                executions.Add(await RunWithTimeoutAsync(directoryCommand.FileName, AddTestFilter(directoryCommand.Arguments, filter), root, remaining, token, "test"));
            }
            else if (Directory.Exists(path) && Directory.EnumerateFiles(path, "*.py", SearchOption.AllDirectories).Any())
            {
                executions.Add(await RunWithTimeoutAsync(PythonExecutable(root), $"-m pytest \"{path}\"", root, remaining, token, "test"));
            }
            else
            {
                executions.Add(await RunWithTimeoutAsync("dotnet", AddTestFilter($"test \"{path}\"", filter), root, remaining, token, "test"));
            }

            if (!executions[^1].Success)
            {
                break;
            }
        }

        return CombineExecutions(executions, selectors.Count, timer.Elapsed);
    }

    public static string AddTestFilter(string arguments, string? filter) => string.IsNullOrWhiteSpace(filter) ? arguments : arguments + " --filter \"" + filter.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    public static (string FileName, string Arguments) BuildCSharpFileTestCommand(string root, string path)
    {
        var file = Path.GetFullPath(NormalizeSelector(root, path));
        var directory = Path.GetDirectoryName(file)!;
        string? project = null;
        for (var current = new DirectoryInfo(directory); current is not null && project is null; current = current.Parent)
            project = current.EnumerateFiles("*.csproj").OrderBy(item => item.FullName, StringComparer.Ordinal).FirstOrDefault()?.FullName;
        if (project is null) throw new FileNotFoundException($"No enclosing .csproj found for '{path}'.");
        var source = File.Exists(file) ? File.ReadAllText(file) : string.Empty;
        var classes = Regex.Matches(source, @"\bclass\s+(\w+)").Select(match => match.Groups[1].Value).Distinct().ToArray();
        var filter = classes.Length == 0 ? string.Empty : " --filter \"" + string.Join("|", classes.Select(name => $"FullyQualifiedName~{name}")) + "\"";
        return ("dotnet", $"test \"{project}\"{filter}");
    }

    public static (string FileName, string Arguments)? BuildCSharpDirectoryTestCommand(string root, string path)
    {
        var directory = Path.GetFullPath(NormalizeSelector(root, path));
        if (!Directory.Exists(directory) || ContainsPytestTestFiles(directory)) return null;
        string? project = null;
        for (var current = new DirectoryInfo(directory); current is not null && project is null; current = current.Parent)
            project = current.EnumerateFiles("*.csproj").OrderBy(item => item.FullName, StringComparer.Ordinal).FirstOrDefault()?.FullName;
        if (project is null)
            project = Directory.EnumerateFiles(directory, "*.csproj", SearchOption.AllDirectories).OrderBy(item => item.Length).ThenBy(item => item, StringComparer.Ordinal).FirstOrDefault();
        if (project is null) return null;
        var classes = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .SelectMany(file =>
            {
                var source = File.ReadAllText(file);
                var isTestFile = Regex.IsMatch(Path.GetFileName(file), @"(?:Tests?|Test)\.cs$", RegexOptions.IgnoreCase)
                    || Regex.IsMatch(source, @"\[(?:Fact|Theory|Test|TestMethod)\b", RegexOptions.IgnoreCase);
                return isTestFile ? Regex.Matches(source, @"\bclass\s+(\w+)").Select(match => match.Groups[1].Value) : Enumerable.Empty<string>();
            })
            .Distinct(StringComparer.Ordinal).ToArray();
        var classFilter = classes.Length == 0 ? string.Empty : " --filter \"" + string.Join("|", classes.Select(name => $"FullyQualifiedName~{name}")) + "\"";
        return ("dotnet", $"test \"{project}\"{classFilter}");
    }

    private static bool ContainsPytestTestFiles(string directory)
        => Directory.EnumerateFiles(directory, "*.py", SearchOption.AllDirectories)
            .Any(file => Path.GetFileName(file).StartsWith("test_", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(file).EndsWith("_test.py", StringComparison.OrdinalIgnoreCase));

    public static (string FileName, string Arguments) BuildPythonFileTestCommand(string root, string path)
    {
        var file = Path.GetFullPath(NormalizeSelector(root, path));
        return (PythonExecutable(root), $"-m pytest \"{file}\"");
    }

    public static (string FileName, string Arguments) BuildPythonScriptTestCommand(string root, string path)
    {
        var file = Path.GetFullPath(NormalizeSelector(root, path));
        return (PythonExecutable(root), $"\"{file}\"");
    }

    public static bool IsStandaloneCheckScript(string root, string path)
    {
        var normalizedPath = Path.GetFullPath(NormalizeSelector(root, path));
        var configPath = Path.Combine(root, ".devx", "config.json");
        if (File.Exists(configPath))
        {
            try
            {
                using var config = JsonDocument.Parse(File.ReadAllText(configPath));
                if (config.RootElement.ValueKind == JsonValueKind.Object
                    && config.RootElement.TryGetProperty("testScriptPattern", out var patternElement)
                    && patternElement.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(patternElement.GetString()))
                {
                    var pattern = patternElement.GetString()!;
                    var relativePath = Path.GetRelativePath(root, normalizedPath);
                    if (Regex.IsMatch(relativePath, pattern) || Regex.IsMatch(normalizedPath, pattern)) return true;
                }
            }
            catch (JsonException) { }
            catch (ArgumentException) { }
        }
        return !LooksLikePytest(normalizedPath);
    }

    public static ProcessStartInfo CreateProcessStartInfo(
        string fileName,
        string arguments,
        string root,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var info = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = workingDirectory ?? root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            if (entry.Key is string key && entry.Value is string value) info.Environment[key] = value;
        if (environment is not null)
            foreach (var pair in environment) info.Environment[pair.Key] = pair.Value;
        return info;
    }

    /// <summary>
    /// Only a command that names a path can be checked cheaply: a bare name is looked up on
    /// PATH by the OS, and guessing at PATH here would refuse commands that work.
    /// </summary>
    private static bool IsMissingExecutable(string fileName, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return true;
        if (!fileName.Contains('/') && !fileName.Contains(Path.DirectorySeparatorChar) && !fileName.Contains(Path.AltDirectorySeparatorChar)) return false;
        var resolved = Path.IsPathRooted(fileName) ? fileName : Path.Combine(workingDirectory, fileName);
        return !File.Exists(resolved);
    }

    /// <summary>
    /// Django's suite lives behind `tests/runtests.py`; there is no pytest entry point and no
    /// project file, so the other runners find nothing and report success on an empty run.
    /// </summary>
    internal static string? DjangoTestScript(string root)
    {
        foreach (var candidate in new[]
        {
            Path.Combine(root, "tests", "runtests.py"),
            Path.Combine(root, "runtests.py")
        })
        {
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static bool LooksLikePytestTestFile(string path)
        => Path.GetFileName(path).StartsWith("test_", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(path).EndsWith("_test.py", StringComparison.OrdinalIgnoreCase)
            || (File.Exists(path) && Regex.IsMatch(File.ReadAllText(path), @"(?m)^\s*(?:async\s+)?def\s+test_"));

    private static bool IsDotnetProject(string root, string? selector)
    {
        if (!string.IsNullOrWhiteSpace(selector))
        {
            var selected = Path.IsPathRooted(selector) ? selector : Path.Combine(root, selector);
            if (selected.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || selected.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return Directory.EnumerateFiles(root, "*.sln", SearchOption.AllDirectories).Any() || Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories).Any();
    }

    private static string GetBuildArguments(string root, string? selector)
    {
        var solution = SelectSolutionFile(root, selector);
        if (solution is not null) return $"build \"{solution}\"";
        var project = SelectProjectFile(root, selector, false) ?? SelectProjectFile(root, selector, true);
        return project is null ? "build" : $"build \"{project}\"";
    }

    private static string GetTestArguments(string root, string? selector)
    {
        var solution = SelectSolutionFile(root, selector);
        if (solution is not null) return $"test \"{solution}\"";
        var project = SelectProjectFile(root, selector, true) ?? SelectProjectFile(root, selector, false);
        return project is null ? "test" : $"test \"{project}\"";
    }

    private static string? SelectSolutionFile(string root, string? selector)
    {
        if (string.IsNullOrWhiteSpace(selector)) return Directory.EnumerateFiles(root, "*.sln", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal).FirstOrDefault();
        var path = NormalizeSelector(root, selector);
        if (path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)) return File.Exists(path) ? path : null;
        return Directory.EnumerateFiles(root, "*.sln", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal).FirstOrDefault(item => Path.GetFileNameWithoutExtension(item).Equals(selector, StringComparison.OrdinalIgnoreCase));
    }

    private static string? SelectProjectFile(string root, string? selector, bool includeTests)
    {
        var candidates = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(selector))
        {
            var path = NormalizeSelector(root, selector);
            if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && File.Exists(path)) return path;
            var match = candidates.FirstOrDefault(item => Path.GetFileNameWithoutExtension(item).Equals(selector, StringComparison.OrdinalIgnoreCase) || item.Contains(selector, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return includeTests ? candidates.FirstOrDefault() : candidates.FirstOrDefault(path => !Path.GetFileNameWithoutExtension(path).Contains("Test", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeSelector(string root, string selector) => Path.IsPathRooted(selector) ? selector : Path.Combine(root, selector);
    private static string PythonExecutable(string root)
    {
        var dotVenvPython = Path.Combine(root, ".venv", "bin", "python");
        if (File.Exists(dotVenvPython)) return dotVenvPython;
        var venvPython = Path.Combine(root, "venv", "bin", "python");
        return File.Exists(venvPython) ? venvPython : "python3";
    }

    private static Task<ExecutionRecord> RunPythonBuildAsync(string root, string? selector, CancellationToken token)
    {
        // py_compile takes one path per argument, so passing the repository's files joins
        // thousands of paths onto a single command line: on a real project that overflows
        // ARG_MAX and the build fails with "Argument list too long" instead of the syntax
        // error the caller asked about. A whole-repo build is one compileall process, with
        // the vendored trees excluded so a dependency's broken file is not reported as yours.
        var args = string.IsNullOrWhiteSpace(selector)
            ? "-m compileall -q -x \"(^|[/\\\\])(\\.?venv|node_modules|site-packages|__pycache__|build|dist|eggs)([/\\\\]|$)\" ."
            : "-m py_compile " + string.Join(' ', PythonFiles(root, selector).Take(200).Select(path => $"\"{path}\""));
        return RunAsync(PythonExecutable(root), args, root, token, "build");
    }

    /// <summary>
    /// Runs a command the repository declared in `.devx/config.json`. The command is the
    /// repository's own, so it runs as written -- including an install step, because a real
    /// build often has to restore its dependencies before it can build anything, and an agent
    /// that is refused does the install by hand anyway, outside any log DevX can see. DevX
    /// still never adds an install step of its own. A repository that wants the refusal back
    /// sets `"blockInstall": true`.
    /// </summary>
    private static Task<ExecutionRecord> RunConfiguredAsync(
        string root,
        DevXCommandConfig config,
        string? target,
        TimeSpan timeout,
        CancellationToken token,
        string runtime)
    {
        var line = config.Render(target);
        if (config.BlockInstall && PackageInstallGuard.IsInstall(line))
        {
            var log = SaveFullLog(root, string.Empty);
            return Task.FromResult(new ExecutionRecord(line, false, log,
                [new DiagnosticRecord("error", "PACKAGE_INSTALL",
                    $"Refused to run a package install from {DevXCommandConfig.RelativePath}: {line}. This repository set \"blockInstall\": true -- run the install yourself and run again.",
                    null, null, null, log)],
                $"FAIL {runtime} — refused a package install: {line}"));
        }

        var (fileName, arguments) = config.BuildCommandLine(target);
        return RunWithTimeoutAsync(fileName, arguments, root, timeout, token, runtime, config.WorkingDirectoryFor(root), config.Environment);
    }

    private static Task<ExecutionRecord> RunPythonTestsAsync(string root, string? selector, CancellationToken token)
    {
        // Django's suite is not a pytest suite: pytest at the root of a Django checkout
        // collects nothing and reports it as a pass, which is how an agent ends up with
        // "0 failed of 0" and no idea whether the code it changed works.
        if (DjangoTestScript(root) is { } runtests && !string.IsNullOrWhiteSpace(selector) && !File.Exists(NormalizeSelector(root, selector)))
        {
            return RunAsync(PythonExecutable(root), $"\"{runtests}\" \"{selector}\"", root, token, "test");
        }

        var target = string.IsNullOrWhiteSpace(selector) ? "" : $" \"{NormalizeSelector(root, selector)}\"";
        if (DjangoTestScript(root) is { } suite && string.IsNullOrWhiteSpace(selector))
        {
            // The whole suite is long and has no natural end, so it gets the same 300 s budget
            // the protocol layer gives any other test run rather than running unbounded.
            return RunWithTimeoutAsync(PythonExecutable(root), $"\"{suite}\"", root, TimeSpan.FromSeconds(300), token, "test");
        }
        return RunAsync(PythonExecutable(root), $"-m pytest{target}", root, token, "test");
    }

    private static bool LooksLikePytest(string path)
    {
        if (!File.Exists(path)) return false;
        var source = File.ReadAllText(path);
        return Regex.IsMatch(source, @"(?m)^\s*(?:async\s+)?def\s+test_[A-Za-z0-9_]*\s*\(")
            || Regex.IsMatch(source, @"(?m)^\s*class\s+Test[A-Za-z0-9_]*\b")
            || Regex.IsMatch(source, @"(?m)^\s*(?:import\s+pytest\b|from\s+pytest\b)");
    }

    private static IReadOnlyList<string> PythonFiles(string root, string? selector)
    {
        if (!string.IsNullOrWhiteSpace(selector))
        {
            var path = NormalizeSelector(root, selector);
            if (File.Exists(path) && path.EndsWith(".py", StringComparison.OrdinalIgnoreCase)) return [path];
        }
        return Directory.EnumerateFiles(root, "*.py", SearchOption.AllDirectories).Where(path => !path.Contains(Path.DirectorySeparatorChar + ".venv" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }

    private static async Task<ExecutionRecord> RunAsync(string fileName, string arguments, string root, CancellationToken token, string? runtime = null)
        => await RunWithTimeoutAsync(fileName, arguments, root, Timeout.InfiniteTimeSpan, token, runtime);

    private static async Task<ExecutionRecord> RunWithTimeoutAsync(
        string fileName,
        string arguments,
        string root,
        TimeSpan timeout,
        CancellationToken token,
        string? runtime = null,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var timer = Stopwatch.StartNew();
        // A configured command that names a path which is not there has to fail as a result,
        // not as an exception from inside DevX: the caller's config is what is wrong.
        if (IsMissingExecutable(fileName, workingDirectory ?? root))
        {
            var startLog = SaveFullLog(root, string.Empty);
            return new ExecutionRecord($"{fileName} {arguments}", false, startLog,
                [new DiagnosticRecord("error", "START_FAILED", $"Command not found: {fileName} (checked relative to {workingDirectory ?? root}).", null, null, null, startLog)],
                $"FAIL {runtime ?? "run"} — command not found: {fileName}");
        }

        var info = CreateProcessStartInfo(fileName, arguments, root, workingDirectory, environment);
        Process process;
        try
        {
            process = Process.Start(info) ?? throw new InvalidOperationException("Failed to start process.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            var startLog = SaveFullLog(root, string.Empty);
            return new ExecutionRecord($"{fileName} {arguments}", false, startLog,
                [new DiagnosticRecord("error", "START_FAILED", $"{fileName}: {exception.Message}", null, null, null, startLog)],
                $"FAIL {runtime ?? "run"} — could not start '{fileName}': {exception.Message}");
        }
        using var owned = process;
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (timeout != Timeout.InfiniteTimeSpan) timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            var timedOutput = string.Join(Environment.NewLine, new[] { await outputTask, await errorTask }.Where(value => !string.IsNullOrWhiteSpace(value)));
            var timeoutLogPath = SaveFullLog(root, timedOutput);
            return new ExecutionRecord(arguments, false, timeoutLogPath,
                [new DiagnosticRecord("error", "TIMEOUT", $"Test execution exceeded {timeout.TotalSeconds:0} seconds.", null, null, null, timeoutLogPath)],
                $"TIMEOUT after {timeout.TotalSeconds:0}s");
        }
        var output = await outputTask;
        var error = await errorTask;
        timer.Stop();
        var combined = string.Join(Environment.NewLine, new[] { output, error }.Where(value => !string.IsNullOrWhiteSpace(value)));
        if (runtime == "test" && fileName == "dotnet" && TryGetFilter(arguments, out var filter) && ReportsZeroDotnetTests(combined))
        {
            var log = SaveFullLog(root, combined);
            return CreateFilteredNoTestsRecord(arguments, filter, log);
        }
        if (process.ExitCode == 0)
        {
            if (runtime == "test" && !HasTestSummary(combined))
            {
                var log = SaveFullLog(root, combined);
                return new ExecutionRecord(arguments, true, log, [], RunSummaryFormatter.FormatTests(combined, true, timer.Elapsed, log, root));
            }
            var successLogPath = SaveFullLog(root, combined);
            var summary = runtime == "test"
                ? RunSummaryFormatter.FormatTests(combined, true, timer.Elapsed, successLogPath, root)
                : runtime == "check"
                    ? RunSummaryFormatter.FormatScript(combined, process.ExitCode, timer.Elapsed, successLogPath, root)
                    : RunSummaryFormatter.FormatBuild(combined, true, timer.Elapsed, successLogPath, root);
            return new ExecutionRecord(arguments, true, successLogPath, [], summary);
        }
        var logPath = SaveFullLog(root, combined);
        IReadOnlyList<DiagnosticRecord> all = runtime == "check" ? Array.Empty<DiagnosticRecord>() : ParseDiagnostics(combined, logPath, runtime == "test" && fileName == "dotnet" ? "dotnet-test" : runtime);
        var diagnostics = all.Take(10).ToList();
        if (all.Count > diagnostics.Count) diagnostics.Add(new DiagnosticRecord("error", "MORE", $"(+{all.Count - diagnostics.Count} more)", null, null, null, logPath));
        var failureSummary = runtime == "test"
            ? RunSummaryFormatter.FormatTests(combined, false, timer.Elapsed, logPath, root)
            : runtime == "check"
                ? RunSummaryFormatter.FormatScript(combined, process.ExitCode, timer.Elapsed, logPath, root)
                : RunSummaryFormatter.FormatBuild(combined, false, timer.Elapsed, logPath, root);
        return new ExecutionRecord(arguments, false, logPath, diagnostics, failureSummary);
    }

    private static ExecutionRecord TimeoutRecord(string selector, string root, TimeSpan timeout)
    {
        var logPath = SaveFullLog(root, string.Empty);
        return new ExecutionRecord(selector, false, logPath,
            [new DiagnosticRecord("error", "TIMEOUT", $"Test execution exceeded {timeout.TotalSeconds:0} seconds.", selector, null, null, logPath)],
            $"TIMEOUT after {timeout.TotalSeconds:0}s");
    }

    private static ExecutionRecord CombineExecutions(IReadOnlyList<ExecutionRecord> executions, int requestedFiles, TimeSpan elapsed)
    {
        if (executions.Count == 0)
        {
            return new ExecutionRecord("test", false, string.Empty,
                [new DiagnosticRecord("error", "NO_TESTS", "No test files were selected.", null, null, null, string.Empty)]);
        }

        var failed = executions.FirstOrDefault(execution => !execution.Success);
        if (failed is not null)
        {
            return new ExecutionRecord(
                string.Join("; ", executions.Select(execution => execution.Command)),
                false,
                failed.RawOutput,
                executions.SelectMany(execution => execution.Diagnostics).Take(10).ToList(),
                failed.Summary);
        }

        var summaries = executions.Select(execution => execution.Summary).Where(summary => !string.IsNullOrWhiteSpace(summary)).ToList();
        var summary = requestedFiles == 1 && summaries.Count == 1
            ? summaries[0]
            : $"PASS {executions.Count}/{requestedFiles} files in {elapsed.TotalSeconds:0.0}s";
        return new ExecutionRecord(string.Join("; ", executions.Select(execution => execution.Command)), true, string.Empty, [], summary);
    }

    public static bool ReportsZeroDotnetTests(string output)
        => Regex.IsMatch(output, @"No test matches the given testcase filter", RegexOptions.IgnoreCase)
            || Regex.IsMatch(output, @"Total:\s*0\b", RegexOptions.IgnoreCase);

    public static ExecutionRecord CreateFilteredNoTestsRecord(string command, string filter, string logPath)
    {
        var message = $"no tests matched the filter '{filter}' (0 tests ran)";
        return new ExecutionRecord(command, false, logPath,
            [new DiagnosticRecord("error", "DOTNET_FILTER", message, null, null, null, logPath)],
            $"FAIL tests — {message}");
    }

    public static string FormatNoTestSummary(string output, int exitCode)
    {
        var last = string.Join(" | ", output.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).TakeLast(5));
        return $"no test summary was produced (exit code {exitCode}). Last output: {(last.Length == 0 ? "<none>" : last)}";
    }

    private static bool TryGetFilter(string arguments, out string filter)
    {
        var match = Regex.Match(arguments, @"(?:^|\s)--filter\s+""(?<filter>[^""]+)""");
        filter = match.Groups["filter"].Value;
        return match.Success && filter.Length > 0;
    }

    public static bool HasTestSummary(string output)
        => Regex.IsMatch(output, @"\d+\s+passed\b.*?in\s+[0-9.]+s", RegexOptions.IgnoreCase)
            || Regex.IsMatch(output, @"Passed:\s*\d+.*?Duration:", RegexOptions.IgnoreCase | RegexOptions.Singleline)
            || Regex.IsMatch(output, @"Passed!\s+-\s+Failed:\s*\d+,\s*Passed:\s*\d+", RegexOptions.IgnoreCase);

    public static string FormatRunnerSummary(string output, string? runtime, TimeSpan elapsed, int exitCode = 0)
        => RunSummaryFormatter.FormatRunnerSummary(output, runtime, elapsed, exitCode);

    public static int CountWarnings(string output) => RunSummaryFormatter.CountWarnings(output);

    private static string SaveFullLog(string root, string output)
    {
        var directory = Path.Combine(root, ".devx", "execution");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}.log");
        File.WriteAllText(path, output);
        return path;
    }

    public static IReadOnlyList<DiagnosticRecord> ParseDiagnostics(string raw, string logPath, string? runtime = null)
    {
        var diagnostics = new List<DiagnosticRecord>();
        var dotnet = new Regex(@"^(?<file>.*)\((?<line>\d+),(?<column>\d+)\): (?<severity>warning|error) (?<code>[A-Z]+\d+): (?<message>.*)$", RegexOptions.Multiline);
        foreach (Match match in dotnet.Matches(raw)) diagnostics.Add(new DiagnosticRecord(match.Groups["severity"].Value, match.Groups["code"].Value, FirstLine(match.Groups["message"].Value), match.Groups["file"].Value, int.Parse(match.Groups["line"].Value), int.Parse(match.Groups["column"].Value), logPath));
        if (diagnostics.Count > 0) return diagnostics;
        if (runtime != "dotnet-test")
        {
            var pytest = new Regex(@"^(?<file>[^\s:][^:\r\n]*\.py):(?<line>\d+)(?::\d+)?(?:: (?<message>.*))?$", RegexOptions.Multiline);
            foreach (Match match in pytest.Matches(raw)) diagnostics.Add(new DiagnosticRecord("error", "PYTEST", FirstLine(match.Groups["message"].Value), match.Groups["file"].Value, int.Parse(match.Groups["line"].Value), null, logPath));
        }
        if (diagnostics.Count == 0 && runtime == "dotnet-test")
        {
            var message = raw.Split('\n').Select(item => item.Trim()).FirstOrDefault(item => item.Length > 0) ?? ".NET test run failed.";
            diagnostics.Add(new DiagnosticRecord("error", "DOTNET_TEST", FirstLine(message), null, null, null, logPath));
            return diagnostics;
        }
        if (diagnostics.Count == 0)
        {
            if (raw.Contains("Test Run Failed", StringComparison.OrdinalIgnoreCase)
                || raw.Contains("Failed!", StringComparison.OrdinalIgnoreCase)
                || raw.Contains("Failed:", StringComparison.OrdinalIgnoreCase))
            {
                var failedTest = raw.Split('\n').Select(item => item.Trim())
                    .FirstOrDefault(item => item.StartsWith("Failed ", StringComparison.OrdinalIgnoreCase));
                var dotnetMessage = failedTest ?? raw.Split('\n').Select(item => item.Trim()).FirstOrDefault(item => item.Length > 0) ?? ".NET test run failed.";
                diagnostics.Add(new DiagnosticRecord("error", "DOTNET_TEST", FirstLine(dotnetMessage), null, null, null, logPath));
                return diagnostics;
            }
            var location = Regex.Match(raw, @"File ""(?<file>[^""]+\.py)"", line (?<line>\d+)");
            var file = location.Groups["file"].Value;
            var line = location.Success ? int.Parse(location.Groups["line"].Value) : (int?)null;
            var message = raw.Split('\n').Select(item => item.Trim()).FirstOrDefault(item => item.StartsWith("SystemExit:", StringComparison.Ordinal) || item.StartsWith("E ") || item.Contains("no tests ran", StringComparison.OrdinalIgnoreCase)) ?? "Python test collection failed.";
            diagnostics.Add(new DiagnosticRecord("error", "PYTEST", FirstLine(message), string.IsNullOrWhiteSpace(file) ? null : file, line, null, logPath));
        }
        return diagnostics;
    }

    private static string FirstLine(string value) => value.Replace("E   ", "", StringComparison.Ordinal).Split('\n')[0].Trim();
}
