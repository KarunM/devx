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
    static async Task<int> RunBuildAsync(string connectionString, string[] args, string format)
    {
        var sessionId = GetOption(args, "--session");
        if (sessionId is null)
        {
            Console.Error.WriteLine("Missing --session. Build requires explicit session selection.");
            return 1;
        }

        var store = new SqliteStorageInitializer(connectionString);
        await store.InitializeAsync();
        var engine = new ProtocolEngine(new SearchService(store, store), store, store);
        var request = new ProtocolRequest("1.0", ProtocolOperations.RunBuild, sessionId, JsonSerializer.SerializeToElement(new
        {
            runtime = GetOption(args, "--runtime"),
            target = GetOption(args, "--target")
        }));
        var started = DateTimeOffset.UtcNow;
        var response = await engine.ExecuteAsync(request);
        await store.AddOperationMetricAsync(new OperationMetric("build", started, DateTimeOffset.UtcNow - started, response.Success, sessionId, OperationMetadata(response, GetOption(args, "--target")), GetAgentLabel(args)));
        if (format == "json") WriteResponse(response, format);
        else Console.WriteLine(ValidationOutputFormatter.Format(response, "build"));
        return response.Success ? 0 : 1;
    }

    static async Task<int> RunTestsAsync(string connectionString, string[] args, string format)
    {
        var sessionId = GetOption(args, "--session");
        if (sessionId is null)
        {
            Console.Error.WriteLine("Missing --session. Test requires explicit session selection.");
            return 1;
        }

        var store = new SqliteStorageInitializer(connectionString);
        await store.InitializeAsync();
        var engine = new ProtocolEngine(new SearchService(store, store), store, store);
        var testFiles = GetPositionals(args, 1);
        var timeoutSeconds = int.TryParse(GetOption(args, "--timeout"), out var parsedTimeout) && parsedTimeout > 0
            ? parsedTimeout
            : 300;
        var request = new ProtocolRequest("1.0", ProtocolOperations.RunTests, sessionId, JsonSerializer.SerializeToElement(new
        {
            runtime = GetOption(args, "--runtime"),
            target = GetOption(args, "--target"),
            targets = testFiles,
            timeoutSeconds,
            filter = GetOption(args, "--filter")
        }));
        var started = DateTimeOffset.UtcNow;
        var response = await engine.ExecuteAsync(request);
        await store.AddOperationMetricAsync(new OperationMetric("test", started, DateTimeOffset.UtcNow - started, response.Success, sessionId, OperationMetadata(response, GetOption(args, "--target")), GetAgentLabel(args)));
        if (format == "json")
        {
            WriteResponse(response, format);
        }
        else
        {
            Console.WriteLine(ValidationOutputFormatter.Format(response, "test"));
        }
        return response.Success ? 0 : 1;
    }

    // `devx verify [build|test|both] [target...]` -- the build and the relevant tests in ONE call
    // with ONE compact summary. build and test remain available for a caller that wants only one of
    // the two; verify is the "edit then check" call.
    static async Task<int> RunVerifyAsync(string connectionString, string[] args, string format)
    {
        var sessionId = GetOption(args, "--session");
        if (sessionId is null)
        {
            Console.Error.WriteLine("Missing --session. Verify requires a session (add --session <id>, or run from the repository).");
            return 1;
        }

        var positionals = GetPositionals(args, 1);
        var mode = positionals.Count > 0 && VerifyAfterEdit.IsModeName(positionals[0])
            ? VerifyAfterEdit.ParseMode(positionals[0])
            : VerifyMode.Both;
        var targets = positionals.Count > 0 && VerifyAfterEdit.IsModeName(positionals[0])
            ? positionals.Skip(1).ToArray()
            : positionals.ToArray();
        var timeoutSeconds = int.TryParse(GetOption(args, "--timeout"), out var parsedTimeout) && parsedTimeout > 0
            ? parsedTimeout
            : 300;

        var store = new SqliteStorageInitializer(connectionString);
        await store.InitializeAsync();
        var engine = new ProtocolEngine(new SearchService(store, store), store, store);
        var request = new ProtocolRequest("1.0", ProtocolOperations.RunVerify, sessionId, JsonSerializer.SerializeToElement(new
        {
            runtime = GetOption(args, "--runtime"),
            target = GetOption(args, "--target"),
            mode = VerifyAfterEdit.ModeName(mode),
            targets,
            timeoutSeconds,
            filter = GetOption(args, "--filter")
        }));
        var started = DateTimeOffset.UtcNow;
        var response = await engine.ExecuteAsync(request);
        await store.AddOperationMetricAsync(new OperationMetric("verify", started, DateTimeOffset.UtcNow - started, response.Success, sessionId, OperationMetadata(response, GetOption(args, "--target")), GetAgentLabel(args)));
        if (format == "json") WriteResponse(response, format);
        else Console.WriteLine(ValidationOutputFormatter.FormatVerify(response));
        return response.Success ? 0 : 1;
    }

    static async Task<int> DiagnosticsAsync(string connectionString, string[] args, string format)
    {
        var sessionId = GetOption(args, "--session");
        if (sessionId is null)
        {
            Console.Error.WriteLine("Missing --session.");
            return 1;
        }

        var store = new SqliteStorageInitializer(connectionString);
        await store.InitializeAsync();
        var engine = new ProtocolEngine(new SearchService(store, store), store, store);
        var request = new ProtocolRequest("1.0", ProtocolOperations.GetDiagnostics, sessionId, JsonSerializer.Deserialize<JsonElement>("{}"));
        var started = DateTimeOffset.UtcNow;
        var response = await engine.ExecuteAsync(request);
        await store.AddOperationMetricAsync(new OperationMetric("diagnostics", started, DateTimeOffset.UtcNow - started, response.Success, sessionId, OperationMetadata(response), GetAgentLabel(args)));
        WriteResponse(response, format);
        return response.Success ? 0 : 1;
    }
}
