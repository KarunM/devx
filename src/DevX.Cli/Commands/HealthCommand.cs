using System.Text.Json;
using DevX.Application;
using DevX.Domain;
using DevX.Cli;
using DevX.Infrastructure.Storage.SQLite;
using DevX.Indexer;
using DevX.Protocol;
using Microsoft.Data.Sqlite;

static partial class Program
{
    static async Task<int> FallbackAsync(string connectionString, string[] args, string format)
    {
        if (args.Length > 1 && args[1] == "observe") return await ShellObservationCommand.RunAsync(connectionString, args, format);
        if (args.Length < 2 || !string.Equals(args[1], "record", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Usage: devx fallback record --session <id> --capability <id> --failed-command <command> --reason <reason> --action <fallback-action>");
            return 1;
        }

        var sessionId = GetOption(args, "--session");
        var capabilityId = GetOption(args, "--capability");
        var failedCommand = GetOption(args, "--failed-command");
        var reason = GetOption(args, "--reason");
        var action = GetOption(args, "--action");

        if (sessionId is null || capabilityId is null || failedCommand is null || reason is null || action is null)
        {
            Console.Error.WriteLine("Missing required fallback metadata.");
            return 1;
        }

        var store = new SqliteStorageInitializer(connectionString);
        await store.InitializeAsync();
        var metadata = new FallbackEventMetadata(capabilityId, failedCommand, reason, action);
        await store.AddOperationMetricAsync(new OperationMetric("fallback", DateTimeOffset.UtcNow, TimeSpan.Zero, false, sessionId, JsonSerializer.Serialize(metadata), GetAgentLabel(args)));

        if (format == "json")
        {
            Console.WriteLine(JsonSerializer.Serialize(new { Success = true, metadata.CapabilityId, metadata.FailedCommand, metadata.Reason, metadata.FallbackAction }));
        }
        else
        {
            Console.WriteLine("Fallback event recorded.");
        }

        return 0;
    }

    static async Task<int> HealthAsync(string connectionString, string[] args, string format)
    {
        var store = new SqliteStorageInitializer(connectionString);
        await store.InitializeAsync();

        var (repositoryRoot, indexedFiles, repositoryIndexed, sessionId) = await ResolveRuntimeReadinessInputsAsync(store, args);
        var health = await new RuntimeHealthService().EvaluateAsync(repositoryRoot, repositoryIndexed, indexedFiles, indexedPaths: await store.GetIndexedFilePathsAsync(repositoryRoot));

        if (sessionId is not null)
        {
            await store.AddOperationMetricAsync(new OperationMetric("health", DateTimeOffset.UtcNow, TimeSpan.Zero, health.Status != "Unavailable", sessionId, AgentLabel: GetAgentLabel(args)));
        }

        if (format == "json")
        {
            Console.WriteLine(JsonSerializer.Serialize(health));
            return health.Status == "Unavailable" ? 1 : 0;
        }

        Console.WriteLine("DevX Health");
        Console.WriteLine($"Status: {health.Status}");
        Console.WriteLine($"Workspace: {health.Context.WorkspaceRoot}");
        Console.WriteLine($"Runtime: {health.Context.RuntimeFamily}");
        if (!string.IsNullOrWhiteSpace(health.Context.ProjectPath))
        {
            Console.WriteLine($"Target: {health.Context.ProjectPath}");
        }

        foreach (var check in health.Checks)
        {
            Console.WriteLine($"- {check.Name}: {check.Status} — {check.Message}");
        }

        return health.Status == "Unavailable" ? 1 : 0;
    }

    static async Task<int> DoctorAsync(string connectionString, string[] args, string format)
    {
        var store = new SqliteStorageInitializer(connectionString);
        await store.InitializeAsync();

        var (repositoryRoot, indexedFiles, repositoryIndexed, sessionId) = await ResolveRuntimeReadinessInputsAsync(store, args);
        var service = new RuntimeHealthService();
        var health = await service.EvaluateAsync(repositoryRoot, repositoryIndexed, indexedFiles, indexedPaths: await store.GetIndexedFilePathsAsync(repositoryRoot));
        var doctor = service.BuildDoctor(health);

        if (sessionId is not null)
        {
            await store.AddOperationMetricAsync(new OperationMetric("doctor", DateTimeOffset.UtcNow, TimeSpan.Zero, doctor.Health.Status != "Unavailable", sessionId, AgentLabel: GetAgentLabel(args)));
        }

        // Which engine answers `devx search text` is a real diagnostic: with no ripgrep the
        // in-process scan answers, and a caller comparing two machines needs to know that.
        var searchEngine = DevX.Infrastructure.Storage.SQLite.RipgrepLocator.Locate(repositoryRoot);

        if (format == "json")
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                doctor.Health,
                doctor.Findings,
                SearchEngine = new { searchEngine.Engine, searchEngine.Executable, searchEngine.Version, Description = searchEngine.Describe() },
            }));
            return doctor.Health.Status == "Unavailable" ? 1 : 0;
        }

        Console.WriteLine("DevX Doctor");
        Console.WriteLine($"Status: {doctor.Health.Status}");
        Console.WriteLine($"Search engine: {searchEngine.Describe()}");
        foreach (var finding in doctor.Findings)
        {
            Console.WriteLine($"- [{finding.Severity}] {finding.Summary}");
            Console.WriteLine($"  Cause: {finding.Cause}");
            Console.WriteLine($"  Remediation: {finding.Remediation}");
        }

        if (doctor.Findings.Count == 0)
        {
            Console.WriteLine("No material runtime findings.");
        }

        return doctor.Health.Status == "Unavailable" ? 1 : 0;
    }
}
