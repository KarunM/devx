using System.Text.Json;
using System.Text.RegularExpressions;
using DevX.Domain;
using DevX.Infrastructure.Storage.SQLite;

namespace DevX.Cli;

public sealed record ObservedFile(string Path, string Classification);
public sealed record ShellObservation(string Command, string Classification, IReadOnlyList<ObservedFile> Files);

public static class ShellObservationCommand
{
    private static readonly HashSet<string> Tools = new(StringComparer.Ordinal) { "grep", "cat", "sed", "head", "tail", "rg", "find" };
    private static readonly HashSet<string> Generated = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".devx", "logs", "log", "bin", "obj", "node_modules", "dist", "build", "out", "coverage", "__pycache__", ".venv", "venv", "generated" };

    // Deliberately conservative, not a shell interpreter. Never execute the observed command.
    public static ShellObservation Classify(string root, string command, IReadOnlyList<string> indexed, IReadOnlyList<string>? suppliedPaths = null)
    {
        ShellObservation Unknown() => new(command, "unclassified", []);
        if (command.IndexOfAny(['|', ';', '&', '<', '>', '`', '$', '\n', '\r', '*', '?', '[', ']']) >= 0) return Unknown();
        var tokens = Regex.Matches(command, "\"[^\"]*\"|'[^']*'|[^\\s]+")
            .Select(m => m.Value.Trim('\'', '"')).ToArray();
        if (tokens.Length == 0 || !Tools.Contains(Path.GetFileName(tokens[0]))) return Unknown();
        var tool = Path.GetFileName(tokens[0]);
        var paths = new List<string>();
        if (suppliedPaths is { Count: > 0 }) paths.AddRange(suppliedPaths);
        else
        {
            var skipPattern = tool is "grep" or "rg" or "sed";
            for (var i = 1; i < tokens.Length; i++)
            {
                if (tokens[i] == "--") { paths.AddRange(tokens.Skip(i + 1)); break; }
                if (tokens[i].StartsWith('-'))
                {
                    if (tokens[i] is "-n" or "-c" && tool is "head" or "tail") { i++; continue; }
                    // Complex options may consume operands: do not claim a classification.
                    if (tokens[i] is "-e" or "-f" or "--glob" or "--include" or "--exclude" or "--files") return Unknown();
                    if (tool == "find") break;
                    continue;
                }
                if (skipPattern) { skipPattern = false; continue; }
                paths.Add(tokens[i]);
            }
        }
        if (paths.Count == 0) return Unknown();
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var results = new List<ObservedFile>();
        foreach (var path in paths)
        {
            var absolute = Path.GetFullPath(Path.Combine(root, path));
            var relative = Path.GetRelativePath(root, absolute).Replace('\\', '/');
            if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal)) { results.Add(new(path, "unclassified")); continue; }
            var info = new FileInfo(absolute);
            var linkedParent = false;
            for (var parent = Directory.GetParent(absolute); parent is not null && parent.FullName != root; parent = parent.Parent)
                if (parent.LinkTarget is not null) { linkedParent = true; break; }
            if (info.LinkTarget is not null || linkedParent) { results.Add(new(path, "unclassified")); continue; }
            var generated = relative.Split('/').Any(p => Generated.Contains(p)) || relative.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
                || relative.Contains(".generated.", StringComparison.OrdinalIgnoreCase) || relative.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase);
            var hasIndexed = indexed.Any(p => p.Replace('\\', '/') == relative
                || p.Replace('\\', '/').StartsWith(relative == "." ? "" : relative.TrimEnd('/') + "/", StringComparison.Ordinal));
            results.Add(new(path, generated || !hasIndexed ? "allowed" : "bypass candidate"));
        }
        return new(command, results.Any(f => f.Classification == "bypass candidate") ? "bypass candidate"
            : results.Any(f => f.Classification == "unclassified") ? "unclassified" : "allowed", results);
    }

    public static async Task<int> RunAsync(string connectionString, string[] args, string format)
    {
        try
        {
            string? Option(string name) => AnalyticsReportCommand.Options(args, name).FirstOrDefault();
            var session = Option("--session") ?? throw new ArgumentException("fallback observe requires --session.");
            if (!Guid.TryParse(session, out var id)) throw new ArgumentException("Invalid session ID.");
            var store = new SqliteStorageInitializer(connectionString); await store.InitializeAsync();
            var state = await store.GetSessionAsync(new GetSessionRequest(new SessionId(id))) ?? throw new ArgumentException("Session not found.");
            var root = await store.GetRepositoryRootPathAsync(state.RepositoryId) ?? throw new ArgumentException("Repository not found.");
            var agent = Option("--agent") ?? Environment.GetEnvironmentVariable("DEVX_AGENT");
            var startText = Option("--coverage-start"); var endText = Option("--coverage-end");
            var command = Option("--command");
            if (command is null && startText is null) throw new ArgumentException("Provide --command or a completed --coverage-start/--coverage-end interval.");
            if (startText is not null || endText is not null)
            {
                if (!DateTimeOffset.TryParse(startText, out var start) || !DateTimeOffset.TryParse(endText, out var end) || end < start || end > DateTimeOffset.UtcNow)
                    throw new ArgumentException("Coverage requires a valid completed interval; future coverage cannot be claimed.");
                await store.AddOperationMetricAsync(new OperationMetric("fallback coverage", DateTimeOffset.UtcNow, TimeSpan.Zero, true, session,
                    JsonSerializer.Serialize(new { CoverageStart = start, CoverageEnd = end }), agent));
            }
            ShellObservation? observation = null;
            if (command is not null)
            {
                observation = Classify(root, command, await store.GetIndexedFilePathsAsync(root), AnalyticsReportCommand.Options(args, "--file"));
                await store.AddOperationMetricAsync(new OperationMetric("fallback observe", DateTimeOffset.UtcNow, TimeSpan.Zero, true, session, JsonSerializer.Serialize(observation), agent));
            }
            Console.WriteLine(format == "json" ? JsonSerializer.Serialize(new { Success = true, Observation = observation }) : "Shell observation recorded; not a compliance assertion.");
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        { Console.Error.WriteLine(ex.Message); return 1; }
    }
}
