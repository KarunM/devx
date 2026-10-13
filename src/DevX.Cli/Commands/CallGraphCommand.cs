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
    static async Task<int> ReferencesAsync(string connectionString, string[] args, string format)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: devx references <symbol> --session <id>");
            return 1;
        }

        var sessionId = GetOption(args, "--session");
        if (sessionId is null)
        {
            Console.Error.WriteLine("Missing --session.");
            return 1;
        }

        var store = new SqliteStorageInitializer(connectionString);
        await store.InitializeAsync();
        var engine = new ProtocolEngine(new SearchService(store, store), store, store);
        var request = new ProtocolRequest("1.0", ProtocolOperations.GetReferences, sessionId, JsonSerializer.SerializeToElement(new { query = args[1] }));
        var started = DateTimeOffset.UtcNow;
        var response = await engine.ExecuteAsync(request);
        await RecordMetricAsync(store, new OperationMetric("get references", started, DateTimeOffset.UtcNow - started, response.Success, sessionId, OperationMetadata(response, args[1]), GetAgentLabel(args)));
        // Text-backed (Python) lookups list their candidates exactly like `read` does.
        var ambiguousTarget = response.Success || format == "json" ? null : response.Errors.FirstOrDefault(error => error.Code == "AmbiguousMatch" && error.Candidates is { Count: > 0 });
        if (ambiguousTarget is not null)
        {
            Console.WriteLine(FormatAmbiguousCandidates(ambiguousTarget, args[1]));
            return 2;
        }
        WriteResponse(response, format);
        return response.Success ? 0 : 1;
    }

    static async Task<int> CallersAsync(string connectionString, string[] args, string format)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: devx callers <method> [--full] --session <id>. Overloads of one method are answered grouped per overload.");
            return 1;
        }

        var sessionId = GetOption(args, "--session");
        if (sessionId is null)
        {
            Console.Error.WriteLine("Missing --session.");
            return 1;
        }

        var store = new SqliteStorageInitializer(connectionString);
        await store.InitializeAsync();
        var engine = new ProtocolEngine(new SearchService(store, store), store, store);
        var request = new ProtocolRequest("1.0", ProtocolOperations.GetCallers, sessionId, JsonSerializer.SerializeToElement(new { query = args[1], full = HasFlag(args, "--full") }));
        var started = DateTimeOffset.UtcNow;
        var response = await engine.ExecuteAsync(request);
        await RecordMetricAsync(store, new OperationMetric("get callers", started, DateTimeOffset.UtcNow - started, response.Success, sessionId, OperationMetadata(response, args[1]), GetAgentLabel(args)));
        // Text-backed (Python) lookups list their candidates exactly like `read` does.
        var ambiguousTarget = response.Success || format == "json" ? null : response.Errors.FirstOrDefault(error => error.Code == "AmbiguousMatch" && error.Candidates is { Count: > 0 });
        if (ambiguousTarget is not null)
        {
            Console.WriteLine(FormatAmbiguousCandidates(ambiguousTarget, args[1]));
            return 2;
        }
        WriteResponse(response, format);
        return response.Success ? 0 : 1;
    }

    static async Task<int> CalleesAsync(string connectionString, string[] args, string format)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: devx callees <method> --session <id>. Overloads of one method are answered grouped per overload.");
            return 1;
        }

        var sessionId = GetOption(args, "--session");
        if (sessionId is null)
        {
            Console.Error.WriteLine("Missing --session.");
            return 1;
        }

        var store = new SqliteStorageInitializer(connectionString);
        await store.InitializeAsync();
        var engine = new ProtocolEngine(new SearchService(store, store), store, store);
        var request = new ProtocolRequest("1.0", ProtocolOperations.GetCallees, sessionId, JsonSerializer.SerializeToElement(new { query = args[1] }));
        var started = DateTimeOffset.UtcNow;
        var response = await engine.ExecuteAsync(request);
        await RecordMetricAsync(store, new OperationMetric("get callees", started, DateTimeOffset.UtcNow - started, response.Success, sessionId, OperationMetadata(response, args[1]), GetAgentLabel(args)));
        WriteResponse(response, format);
        return response.Success ? 0 : 1;
    }
}
