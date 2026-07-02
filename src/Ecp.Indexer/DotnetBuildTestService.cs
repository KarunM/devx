using System.Diagnostics;
using System.Text.RegularExpressions;
using Ecp.Domain;

namespace Ecp.Indexer;

public sealed class DotnetBuildTestService
{
    public async Task<ExecutionRecord> RunBuildAsync(string repositoryRoot, CancellationToken cancellationToken = default)
        => await RunAsync("dotnet", GetBuildArguments(repositoryRoot), repositoryRoot, cancellationToken);

    public async Task<ExecutionRecord> RunTestsAsync(string repositoryRoot, CancellationToken cancellationToken = default)
        => await RunAsync("dotnet", GetTestArguments(repositoryRoot), repositoryRoot, cancellationToken);

    private static string GetBuildArguments(string repositoryRoot)
    {
        var solution = FindSolutionFile(repositoryRoot);
        if (solution is not null)
        {
            return $"build \"{solution}\"";
        }

        var project = FindProjectFile(repositoryRoot, includeTestProjects: false) ?? FindProjectFile(repositoryRoot, includeTestProjects: true);
        return project is null ? "build" : $"build \"{project}\"";
    }

    private static string GetTestArguments(string repositoryRoot)
    {
        var solution = FindSolutionFile(repositoryRoot);
        if (solution is not null)
        {
            return $"test \"{solution}\"";
        }

        var project = FindProjectFile(repositoryRoot, includeTestProjects: true) ?? FindProjectFile(repositoryRoot, includeTestProjects: false);
        return project is null ? "test" : $"test \"{project}\"";
    }

    private static string? FindSolutionFile(string repositoryRoot)
        => Directory.EnumerateFiles(repositoryRoot, "*.sln", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal).FirstOrDefault();

    private static string? FindProjectFile(string repositoryRoot, bool includeTestProjects)
    {
        var candidates = Directory.EnumerateFiles(repositoryRoot, "*.csproj", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal);

        if (includeTestProjects)
        {
            return candidates.FirstOrDefault();
        }

        return candidates.FirstOrDefault(path => !Path.GetFileNameWithoutExtension(path).Contains("Test", StringComparison.OrdinalIgnoreCase));
    }

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
