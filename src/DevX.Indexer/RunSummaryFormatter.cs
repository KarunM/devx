using System.Text.RegularExpressions;
using DevX.Domain;

namespace DevX.Indexer;

public static class RunSummaryFormatter
{
    public static string FormatBuild(string output, bool success, TimeSpan duration, string logPath, string root)
    {
        var warnings = CountWarnings(output);
        var log = RelativeLog(logPath, root);
        if (success) return $"PASS build in {duration.TotalSeconds:0.0}s ({warnings} warnings) log: {log}";
        var errors = DotnetDiagnostics(output, logPath).Where(item => item.Severity == "error")
            .Select(item => FormatDiagnostic(item, root)).Distinct(StringComparer.Ordinal).ToArray();
        var errorCount = errors.Length;
        var lines = errors.Take(10).ToList();
        lines.Insert(0, $"FAIL build — {errorCount} errors ({warnings} warnings)");
        lines.Add($"log: {log}");
        return string.Join(Environment.NewLine, lines);
    }

    public static string FormatTests(string output, bool success, TimeSpan duration, string logPath, string root)
    {
        var log = RelativeLog(logPath, root);
        var warnings = CountWarnings(output);
        var counts = TestCounts(output);
        if (success && counts.Total > 0)
            return $"PASS {counts.Passed} tests in {duration.TotalSeconds:0.0}s ({warnings} warnings) log: {log}";
        if (success)
            return $"PASS tests — no test summary was produced (exit code 0) ({warnings} warnings) log: {log}";

        var failed = counts.Failed;
        var header = $"FAIL tests — {failed} failed of {counts.Total} ({warnings} warnings) in {duration.TotalSeconds:0.0}s";
        var lines = new List<string> { header };
        var errorLines = DotnetDiagnostics(output, logPath).Where(item => item.Severity == "error").Select(item => FormatDiagnostic(item, root))
            .Concat(PytestDiagnostics(output).Select(item => $"{item.File}:{item.Line}: PYTEST {Trim(item.Message)}"))
            .Distinct(StringComparer.Ordinal)
            .Take(10);
        lines.AddRange(errorLines);
        lines.AddRange(FailingTests(output));
        lines.Add($"log: {log}");
        return string.Join(Environment.NewLine, lines);
    }

    public static string FormatScript(string output, int exitCode, TimeSpan duration, string logPath, string root)
    {
        var log = RelativeLog(logPath, root);
        var lastLine = output.Split('\n').Select(line => line.Trim()).LastOrDefault(line => line.Length > 0) ?? string.Empty;
        var checks = Regex.Match(lastLine, @"^(?<passed>\d+)\s*/\s*(?<total>\d+)\s+checks?\s+passed$", RegexOptions.IgnoreCase);
        if (exitCode == 0)
        {
            return checks.Success
                ? $"PASS {checks.Groups["passed"].Value}/{checks.Groups["total"].Value} checks in {duration.TotalSeconds:0.0}s log: {log}"
                : $"PASS script in {duration.TotalSeconds:0.0}s log: {log}";
        }

        if (checks.Success)
        {
            var passed = int.Parse(checks.Groups["passed"].Value);
            var total = int.Parse(checks.Groups["total"].Value);
            var lines = new List<string> { $"FAIL checks — {Math.Max(0, total - passed)} failed of {total} in {duration.TotalSeconds:0.0}s" };
            lines.AddRange(output.Split('\n').Select(line => line.Trim()).Where(line => line.Contains("FAIL", StringComparison.Ordinal)).Take(10).Select(Trim));
            lines.Add($"log: {log}");
            return string.Join(Environment.NewLine, lines);
        }

        var crashLines = output.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).TakeLast(5).Select(Trim);
        return string.Join(Environment.NewLine, new[] { $"FAIL script — exit code {exitCode}" }.Concat(crashLines).Append($"log: {log}"));
    }

    public static int CountWarnings(string output)
    {
        var dotnetWarnings = Regex.Matches(output, @"(?m)^.*\bwarning\s+[A-Z]+\d+\b.*$").Select(match => match.Value.Trim()).Distinct(StringComparer.Ordinal).Count();
        if (dotnetWarnings > 0) return dotnetWarnings;
        var warningMatch = Regex.Match(output, @"(?i)\b(\d+) warnings?\b");
        return warningMatch.Success && int.TryParse(warningMatch.Groups[1].Value, out var warnings) ? warnings : 0;
    }

    public static string FormatRunnerSummary(string output, string? runtime, TimeSpan elapsed, int exitCode = 0)
    {
        if (runtime == "test")
        {
            var counts = TestCounts(output);
            if (counts.Total == 0) return $"no test summary was produced (exit code {exitCode}). Last output: {(output.Length == 0 ? "<none>" : string.Join(" | ", output.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).TakeLast(5)))}";
            var pytest = Regex.Match(output, @"(?<count>\d+)\s+passed\b.*?in\s+(?<time>[0-9.]+s)", RegexOptions.IgnoreCase);
            if (pytest.Success) return $"PASS {pytest.Groups["count"].Value} tests in {pytest.Groups["time"].Value}";
            var dotnet = Regex.Match(output, @"Passed:\s*(?<count>\d+).*?Duration:\s*(?<time>[^\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (dotnet.Success) return $"PASS {dotnet.Groups["count"].Value} tests in {dotnet.Groups["time"].Value.Trim()}";
            return counts.Passed > 0 ? $"PASS {counts.Passed} tests" : $"PASS tests in {elapsed.TotalSeconds:0.0}s";
        }
        if (runtime == "check")
        {
            var checks = Regex.Match(output, @"(?<passed>\d+)\s*/\s*(?<total>\d+)\s+checks?\s+passed", RegexOptions.IgnoreCase);
            return checks.Success ? $"PASS {checks.Groups["passed"].Value}/{checks.Groups["total"].Value} checks in {elapsed.TotalSeconds:0.0}s" : $"PASS check script in {elapsed.TotalSeconds:0.0}s";
        }
        return $"PASS build in {elapsed.TotalSeconds:0.0}s";
    }

    private static (int Failed, int Passed, int Total) TestCounts(string output)
    {
        var dotnet = Regex.Match(output, @"Failed:\s*(?<failed>\d+).*?Passed:\s*(?<passed>\d+).*?Total:\s*(?<total>\d+)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!dotnet.Success) dotnet = Regex.Match(output, @"Passed!\s+-\s+Failed:\s*(?<failed>\d+),\s*Passed:\s*(?<passed>\d+).*?Total:\s*(?<total>\d+)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (dotnet.Success) return (Number(dotnet, "failed"), Number(dotnet, "passed"), Number(dotnet, "total"));
        var pytest = Regex.Match(output, @"(?<failed>\d+) failed(?:,\s*(?<passed>\d+) passed)?|(?<passed>\d+) passed(?:,\s*(?<failed>\d+) failed)?", RegexOptions.IgnoreCase);
        return pytest.Success ? (Number(pytest, "failed"), Number(pytest, "passed"), Number(pytest, "failed") + Number(pytest, "passed")) : (0, 0, 0);
    }

    private static int Number(Match match, string group) => int.TryParse(match.Groups[group].Value, out var value) ? value : 0;

    private static IEnumerable<DiagnosticRecord> DotnetDiagnostics(string output, string logPath)
    {
        var regex = new Regex(@"^(?<file>.*)\((?<line>\d+),(?<column>\d+)\): (?<severity>warning|error) (?<code>[A-Z]+\d+): (?<message>.*)$", RegexOptions.Multiline);
        foreach (Match match in regex.Matches(output))
            yield return new DiagnosticRecord(match.Groups["severity"].Value, match.Groups["code"].Value, match.Groups["message"].Value, match.Groups["file"].Value, int.Parse(match.Groups["line"].Value), int.Parse(match.Groups["column"].Value), logPath);
    }

    private static IEnumerable<DiagnosticRecord> PytestDiagnostics(string output)
    {
        var regex = new Regex(@"^(?<file>[^\s:][^:\r\n]*\.py):(?<line>\d+):\s*(?<message>AssertionError(?::.*)?|Error(?::.*)?|E\s+.+)$", RegexOptions.Multiline);
        foreach (Match match in regex.Matches(output))
            yield return new DiagnosticRecord("error", "PYTEST", match.Groups["message"].Value, match.Groups["file"].Value, int.Parse(match.Groups["line"].Value), null, string.Empty);
    }

    private static string FormatDiagnostic(DiagnosticRecord item, string root)
    {
        var message = Regex.Replace(item.Message, @"\s+\[[^\]\r\n]+\.csproj\]\s*$", string.Empty, RegexOptions.IgnoreCase);
        var file = item.File;
        if (!string.IsNullOrWhiteSpace(file) && Path.IsPathRooted(file)) file = Path.GetRelativePath(root, file);
        return $"{file ?? "<unknown>"}:{item.Line?.ToString() ?? "0"}: {item.Code} {Trim(message)}";
    }

    private static IEnumerable<string> FailingTests(string output)
    {
        var matches = Regex.Matches(output, @"(?im)^(?:Failed\s+(?<xunit>.+?)\s+\[FAIL\]|FAILED\s+(?<pytest>\S+)(?:\s+-\s*(?<inline>.*))?)\s*$");
        foreach (Match match in matches)
        {
            var name = match.Groups["xunit"].Success ? match.Groups["xunit"].Value : match.Groups["pytest"].Value;
            var assertion = match.Groups["inline"].Value;
            if (assertion.Length == 0)
            {
                var end = matches.Cast<Match>().FirstOrDefault(next => next.Index > match.Index)?.Index ?? output.Length;
                assertion = output[(match.Index + match.Length)..end].Split('\n')
                    .Select(line => line.Trim())
                    .FirstOrDefault(line => line.StartsWith("Assert.", StringComparison.Ordinal)
                        || line.StartsWith("E AssertionError", StringComparison.Ordinal)
                        || line.StartsWith("assert ", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
            }
            yield return Trim(assertion.Length == 0 ? name : $"{name}: {assertion}");
        }
    }

    private static string Trim(string value)
    {
        var oneLine = Regex.Replace(value, @"\s+", " ").Trim();
        return oneLine.Length <= 200 ? oneLine : oneLine[..200];
    }

    private static string RelativeLog(string logPath, string root)
        => string.IsNullOrWhiteSpace(logPath) ? logPath : Path.GetRelativePath(root, logPath);
}
