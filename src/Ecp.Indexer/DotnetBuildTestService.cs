using System.Diagnostics;
using System.Text.RegularExpressions;
using Ecp.Domain;

namespace Ecp.Indexer;

public sealed class DotnetBuildTestService
{
    public async Task<ExecutionRecord> RunBuildAsync(string repositoryRoot, CancellationToken cancellationToken = default)
        => await RunAsync("dotnet", GetBuildArguments(repositoryRoot, null), repositoryRoot, cancellationToken);

    public async Task<ExecutionRecord> RunBuildAsync(string repositoryRoot, string? projectSelector, CancellationToken cancellationToken = default)
        => await RunAsync("dotnet", GetBuildArguments(repositoryRoot, projectSelector), repositoryRoot, cancellationToken);

    public async Task<ExecutionRecord> RunTestsAsync(string repositoryRoot, CancellationToken cancellationToken = default)
        => await RunAsync("dotnet", GetTestArguments(repositoryRoot, null), repositoryRoot, cancellationToken);

    public async Task<ExecutionRecord> RunTestsAsync(string repositoryRoot, string? projectSelector, CancellationToken cancellationToken = default)
        => await RunAsync("dotnet", GetTestArguments(repositoryRoot, projectSelector), repositoryRoot, cancellationToken);

    private static string GetBuildArguments(string repositoryRoot, string? projectSelector)
    {
        var solution = SelectSolutionFile(repositoryRoot, projectSelector);
        if (solution is not null)
        {
            return $"build \"{solution}\"";
        }

        var project = SelectProjectFile(repositoryRoot, projectSelector, includeTestProjects: false)
            ?? SelectProjectFile(repositoryRoot, projectSelector, includeTestProjects: true);
        return project is null ? "build" : $"build \"{project}\"";
    }

    private static string GetTestArguments(string repositoryRoot, string? projectSelector)
    {
        var solution = SelectSolutionFile(repositoryRoot, projectSelector);
        if (solution is not null)
        {
            return $"test \"{solution}\"";
        }

        var project = SelectProjectFile(repositoryRoot, projectSelector, includeTestProjects: true)
            ?? SelectProjectFile(repositoryRoot, projectSelector, includeTestProjects: false);
        return project is null ? "test" : $"test \"{project}\"";
    }

    private static string? SelectSolutionFile(string repositoryRoot, string? projectSelector)
    {
        if (string.IsNullOrWhiteSpace(projectSelector))
        {
            return Directory.EnumerateFiles(repositoryRoot, "*.sln", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal).FirstOrDefault();
        }

        var selectorPath = NormalizeSelector(repositoryRoot, projectSelector);
        if (selectorPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
        {
            return System.IO.File.Exists(selectorPath) ? selectorPath : null;
        }

        return Directory.EnumerateFiles(repositoryRoot, "*.sln", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .FirstOrDefault(path => Path.GetFileNameWithoutExtension(path).Equals(projectSelector, StringComparison.OrdinalIgnoreCase));
    }

    private static string? SelectProjectFile(string repositoryRoot, string? projectSelector, bool includeTestProjects)
    {
        var candidates = Directory.EnumerateFiles(repositoryRoot, "*.csproj", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(projectSelector))
        {
            var selectorPath = NormalizeSelector(repositoryRoot, projectSelector);
            if (selectorPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(selectorPath))
            {
                return selectorPath;
            }

            var selectorMatch = candidates.FirstOrDefault(path =>
                Path.GetFileNameWithoutExtension(path).Equals(projectSelector, StringComparison.OrdinalIgnoreCase) ||
                path.Contains(projectSelector, StringComparison.OrdinalIgnoreCase));

            if (selectorMatch is not null)
            {
                return selectorMatch;
            }
        }

        if (includeTestProjects)
        {
            return candidates.FirstOrDefault();
        }

        return candidates.FirstOrDefault(path => !Path.GetFileNameWithoutExtension(path).Contains("Test", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeSelector(string repositoryRoot, string selector)
        => Path.IsPathRooted(selector) ? selector : Path.Combine(repositoryRoot, selector);

    private static async Task<ExecutionRecord> RunAsync(string fileName, string arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start process.");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var combined = string.Join(Environment.NewLine, new[] { output, error }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var diagnostics = ParseDiagnostics(combined);
        return new ExecutionRecord(arguments, process.ExitCode == 0, combined, diagnostics);
    }

    private static IReadOnlyList<DiagnosticRecord> ParseDiagnostics(string rawOutput)
    {
        var diagnostics = new List<DiagnosticRecord>();
        var regex = new Regex(@"^(?<file>.*)\((?<line>\d+),(?<column>\d+)\): (?<severity>warning|error) (?<code>[A-Z]+\d+): (?<message>.*)$", RegexOptions.Multiline);
        foreach (Match match in regex.Matches(rawOutput))
        {
            diagnostics.Add(new DiagnosticRecord(
                match.Groups["severity"].Value,
                match.Groups["code"].Value,
                match.Groups["message"].Value,
                match.Groups["file"].Value,
                int.Parse(match.Groups["line"].Value),
                int.Parse(match.Groups["column"].Value),
                rawOutput));
        }

        return diagnostics;
    }
}
