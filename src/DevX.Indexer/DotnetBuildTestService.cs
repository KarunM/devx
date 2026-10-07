using System.Diagnostics;
using System.Text.RegularExpressions;
using DevX.Domain;
using File = System.IO.File;

namespace DevX.Indexer;

public sealed class DotnetBuildTestService
{
    public Task<ExecutionRecord> RunBuildAsync(string root, CancellationToken token = default) => RunBuildAsync(root, null, token);
    public Task<ExecutionRecord> RunBuildAsync(string root, string? selector, CancellationToken token = default)
        => IsDotnetProject(root, selector) ? RunAsync("dotnet", GetBuildArguments(root, selector), root, token) : RunPythonBuildAsync(root, selector, token);
    public Task<ExecutionRecord> RunTestsAsync(string root, CancellationToken token = default) => RunTestsAsync(root, null, token);
    public Task<ExecutionRecord> RunTestsAsync(string root, string? selector, CancellationToken token = default)
        => IsDotnetProject(root, selector) ? RunAsync("dotnet", GetTestArguments(root, selector), root, token, "test") : RunPythonTestsAsync(root, selector, token);

    public async Task<ExecutionRecord> RunTestsAsync(
        string root,
        IReadOnlyList<string> selectors,
        TimeSpan timeout,
        CancellationToken token = default)
    {
        if (selectors.Count == 0)
        {
            return await RunWithTimeoutAsync(
                IsDotnetProject(root, null) ? "dotnet" : PythonExecutable(root),
                IsDotnetProject(root, null) ? GetTestArguments(root, null) : "-m pytest",
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
            if (path.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
            {
                var pytest = LooksLikePytest(path);
                var arguments = pytest ? $"-m pytest \"{path}\"" : $"\"{path}\"";
                executions.Add(await RunWithTimeoutAsync(PythonExecutable(root), arguments, root, remaining, token, pytest ? "test" : "check"));
            }
            else
            {
                executions.Add(await RunWithTimeoutAsync("dotnet", $"test \"{path}\"", root, remaining, token, "test"));
            }

            if (!executions[^1].Success)
            {
                break;
            }
        }

        return CombineExecutions(executions, selectors.Count, timer.Elapsed);
    }

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
    private static string PythonExecutable(string root) => File.Exists(Path.Combine(root, ".venv", "bin", "python")) ? Path.Combine(root, ".venv", "bin", "python") : "python3";

    private static Task<ExecutionRecord> RunPythonBuildAsync(string root, string? selector, CancellationToken token)
    {
        var files = PythonFiles(root, selector);
        var args = files.Count == 0 ? "-m compileall -q ." : "-m py_compile " + string.Join(' ', files.Select(path => $"\"{path}\""));
        return RunAsync(PythonExecutable(root), args, root, token, "build");
    }

    private static Task<ExecutionRecord> RunPythonTestsAsync(string root, string? selector, CancellationToken token)
    {
        var target = string.IsNullOrWhiteSpace(selector) ? "" : $" \"{NormalizeSelector(root, selector)}\"";
        return RunAsync(PythonExecutable(root), $"-m pytest{target}", root, token, "test");
    }

    private static bool LooksLikePytest(string path)
    {
        if (!File.Exists(path)) return false;
        var source = File.ReadAllText(path);
        return Regex.IsMatch(source, @"(?m)^\s*(?:async\s+)?def\s+test_[A-Za-z0-9_]*\s*\(")
            || Regex.IsMatch(source, @"(?m)^\s*class\s+Test[A-Za-z0-9_]*\b")
            || source.Contains("import pytest", StringComparison.Ordinal);
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

    private static async Task<ExecutionRecord> RunWithTimeoutAsync(string fileName, string arguments, string root, TimeSpan timeout, CancellationToken token, string? runtime = null)
    {
        var timer = Stopwatch.StartNew();
        var info = new ProcessStartInfo(fileName, arguments) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Failed to start process.");
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
        if (process.ExitCode == 0) return new ExecutionRecord(arguments, true, string.Empty, [], SuccessSummary(combined, runtime, timer.Elapsed));
        var logPath = SaveFullLog(root, combined);
        var all = ParseDiagnostics(combined, logPath);
        var diagnostics = all.Take(10).ToList();
        if (all.Count > diagnostics.Count) diagnostics.Add(new DiagnosticRecord("error", "MORE", $"(+{all.Count - diagnostics.Count} more)", null, null, null, logPath));
        return new ExecutionRecord(arguments, false, logPath, diagnostics);
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

    private static string SuccessSummary(string output, string? runtime, TimeSpan elapsed)
    {
        if (runtime == "test")
        {
            var pytest = Regex.Match(output, @"(?<count>\d+)\s+passed\b.*?in\s+(?<time>[0-9.]+s)", RegexOptions.IgnoreCase);
            if (pytest.Success)
            {
                return $"PASS {pytest.Groups["count"].Value} tests in {pytest.Groups["time"].Value}";
            }

            var dotnet = Regex.Match(output, @"Passed:\s*(?<count>\d+).*?Duration:\s*(?<time>[^\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return dotnet.Success
                ? $"PASS {dotnet.Groups["count"].Value} tests in {dotnet.Groups["time"].Value.Trim()}"
                : $"PASS tests in {elapsed.TotalSeconds:0.0}s";
        }
        if (runtime == "check")
        {
            var checks = Regex.Match(output, @"(?<passed>\d+)\s*/\s*(?<total>\d+)\s+checks?\s+passed", RegexOptions.IgnoreCase);
            return checks.Success
                ? $"PASS {checks.Groups["passed"].Value}/{checks.Groups["total"].Value} checks in {elapsed.TotalSeconds:0.0}s"
                : $"PASS check script in {elapsed.TotalSeconds:0.0}s";
        }
        return $"PASS build in {elapsed.TotalSeconds:0.0}s";
    }

    private static string SaveFullLog(string root, string output)
    {
        var directory = Path.Combine(root, ".devx", "execution");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}.log");
        File.WriteAllText(path, output);
        return path;
    }

    private static IReadOnlyList<DiagnosticRecord> ParseDiagnostics(string raw, string logPath)
    {
        var diagnostics = new List<DiagnosticRecord>();
        var dotnet = new Regex(@"^(?<file>.*)\((?<line>\d+),(?<column>\d+)\): (?<severity>warning|error) (?<code>[A-Z]+\d+): (?<message>.*)$", RegexOptions.Multiline);
        foreach (Match match in dotnet.Matches(raw)) diagnostics.Add(new DiagnosticRecord(match.Groups["severity"].Value, match.Groups["code"].Value, FirstLine(match.Groups["message"].Value), match.Groups["file"].Value, int.Parse(match.Groups["line"].Value), int.Parse(match.Groups["column"].Value), logPath));
        if (diagnostics.Count > 0) return diagnostics;
        var pytest = new Regex(@"^(?<file>[^\s:][^:\r\n]*\.py):(?<line>\d+)(?::\d+)?(?:: (?<message>.*))?$", RegexOptions.Multiline);
        foreach (Match match in pytest.Matches(raw)) diagnostics.Add(new DiagnosticRecord("error", "PYTEST", FirstLine(match.Groups["message"].Value), match.Groups["file"].Value, int.Parse(match.Groups["line"].Value), null, logPath));
        if (diagnostics.Count == 0)
        {
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
