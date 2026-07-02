using System.Text.Json;
using System.Security.Cryptography;
using Ecp.Domain;
using Ecp.Cli;
using Ecp.Infrastructure.Storage.SQLite;
using Ecp.Indexer;
using Ecp.Protocol;
using Microsoft.Data.Sqlite;
using IOFile = System.IO.File;

var exitCode = await RunAsync(args);
return exitCode;

static async Task<int> RunAsync(string[] args)
{
    if (args.Length == 0 || args[0] is "--help" or "-h")
    {
        PrintHelp();
        return 0;
    }

    var format = GetOption(args, "--format") ?? "text";
    var command = args[0];
    var databasePath = Path.Combine(".ecp", "ecp.db");
    Directory.CreateDirectory(".ecp");
    var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();

    switch (command)
    {
        case "index":
            if (args.Length >= 2 && args[1] == "status")
            {
                return await IndexStatusAsync(connectionString, args, format);
            }

            if (args.Length < 2)
            {
                Console.Error.WriteLine("Missing repository path.");
                return 1;
            }

            await IndexAsync(connectionString, args[1]);
            return 0;

        case "search":
            return await SearchAsync(connectionString, args, format);

        case "session":
            return await SessionAsync(connectionString, args, format);

        case "patch":
            return await PatchAsync(connectionString, args, format);

        case "build":
            return await RunBuildAsync(connectionString, args, format);

        case "test":
            return await RunTestsAsync(connectionString, args, format);

        case "diagnostics":
            return await DiagnosticsAsync(connectionString, args, format);

        case "snapshot":
            return await SnapshotAsync(connectionString, args, format);

        case "method":
            return await MethodAsync(connectionString, args, format);

        case "type":
            return await TypeAsync(connectionString, args, format);

        case "callers":
            return await CallersAsync(connectionString, args, format);

        case "references":
            return await ReferencesAsync(connectionString, args, format);

        case "read":
            return await ReadAsync(connectionString, args, format);

        case "callees":
            return await CalleesAsync(connectionString, args, format);

        case "report":
            return await ReportAsync(connectionString, args, format);

        default:
            Console.Error.WriteLine($"Unknown command '{command}'.");
            return 1;
    }
}

static async Task IndexAsync(string connectionString, string repositoryPath)
{
    var started = DateTimeOffset.UtcNow;
    var store = new SqliteStorageInitializer(connectionString);
    await store.InitializeAsync();
    var indexer = new RepositoryIndexingService(store);
    await indexer.IndexAsync(repositoryPath);
    await store.AddOperationMetricAsync(new OperationMetric("index", started, DateTimeOffset.UtcNow - started, true, null));
    Console.WriteLine("Indexed repository.");
}

static async Task<int> IndexStatusAsync(string connectionString, string[] args, string format)
{
    var sessionId = GetOption(args, "--session");
    string? repositoryPath = null;

    for (var index = 2; index < args.Length; index++)
    {
        var arg = args[index];
        if (arg.StartsWith("--", StringComparison.Ordinal))
        {
            if (arg == "--session" || arg == "--format")
            {
                index++;
            }
            continue;
        }

        if (arg.Equals("json", StringComparison.OrdinalIgnoreCase) || arg.Equals("text", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        repositoryPath = arg;
        break;
    }

    var started = DateTimeOffset.UtcNow;
    var store = new SqliteStorageInitializer(connectionString);
    await store.InitializeAsync();

    if (string.IsNullOrWhiteSpace(repositoryPath))
    {
        repositoryPath = Path.GetFullPath(Environment.CurrentDirectory);
    }

    var repositoryRoot = Path.GetFullPath(repositoryPath);
    var repository = await store.GetRepositoryByRootPathAsync(repositoryRoot);
    var indexedFiles = repository is null ? 0 : await store.CountIndexedFilesAsync(repository.Id);
    var response = new IndexStatusResponse(
        RepositoryIndexed: repository is not null,
        RepositoryRoot: repositoryRoot,
        IndexedFiles: indexedFiles,
        IndexExists: repository is not null,
        Status: repository is null ? "Missing" : "Current",
        Reason: repository is null ? "Repository has not been indexed yet." : "No obvious stale-index signal detected.");

    if (!string.IsNullOrWhiteSpace(sessionId))
    {
        await store.AddOperationMetricAsync(new OperationMetric("index status", started, DateTimeOffset.UtcNow - started, true, sessionId));
    }

    WriteResponse(new ProtocolResponse(true, JsonSerializer.SerializeToElement(response), [], []), format);
    return 0;
}

static async Task<int> SearchAsync(string connectionString, string[] args, string format)
{
    if (args.Length < 4)
    {
        Console.Error.WriteLine("Usage: ecp search <symbol|text> <query> --session <id>");
        return 1;
    }

    var searchKind = args[1];
    var query = args[2];
    var sessionId = GetOption(args, "--session");
    if (sessionId is null)
    {
        Console.Error.WriteLine("Missing --session.");
        return 1;
    }

    var store = new SqliteStorageInitializer(connectionString);
    await store.InitializeAsync();
    var engine = new ProtocolEngine(new SearchService(store, store), store, store);
    var request = new ProtocolRequest(
        "1.0",
        searchKind == "symbol" ? ProtocolOperations.SearchSymbol : ProtocolOperations.SearchText,
        sessionId,
        JsonSerializer.SerializeToElement(new { query }));

    var started = DateTimeOffset.UtcNow;
    var response = await engine.ExecuteAsync(request);
    await store.AddOperationMetricAsync(new OperationMetric($"search {searchKind}", started, DateTimeOffset.UtcNow - started, response.Success, sessionId));
    WriteResponse(response, format);
    return response.Success ? 0 : 1;
}

static async Task<int> SessionAsync(string connectionString, string[] args, string format)
{
    if (args.Length < 2 || args[1] != "create")
    {
        Console.Error.WriteLine("Usage: ecp session create");
        return 1;
    }

    var store = new SqliteStorageInitializer(connectionString);
    await store.InitializeAsync();
    var repositoryRoot = Path.GetFullPath(Environment.CurrentDirectory);
    var repository = new Repository(new RepositoryId(StableId(repositoryRoot)), Path.GetFileName(repositoryRoot), repositoryRoot);
    await store.InitializeAsync();
    await store.UpsertRepositoryAsync(repository);
    var started = DateTimeOffset.UtcNow;
    var session = await store.CreateSessionAsync(new CreateSessionRequest(repository.Id));
    await store.AddOperationMetricAsync(new OperationMetric("session create", started, DateTimeOffset.UtcNow - started, true, session.Id.Value.ToString()));
    WriteResponse(new ProtocolResponse(true, JsonSerializer.SerializeToElement(new { sessionId = session.Id.Value.ToString() }), [], []), format);
    return 0;
}

static Guid StableId(string value)
{
    var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
    Span<byte> guidBytes = stackalloc byte[16];
    hash.AsSpan(0, 16).CopyTo(guidBytes);
    return new Guid(guidBytes);
}

static async Task<int> PatchAsync(string connectionString, string[] args, string format)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: ecp patch <apply|preview> <patch-file> --session <id>");
        return 1;
    }

    if (args[1] == "validate")
    {
        return await PatchValidateAsync(connectionString, args, format);
    }

    if (args[1] == "rollback")
    {
        return await PatchRollbackAsync(connectionString, args, format);
    }

    if (args[1] == "preview")
    {
        return await PatchPreviewAsync(connectionString, args, format);
    }

    if (args[1] != "apply")
    {
        Console.Error.WriteLine("Usage: ecp patch apply <patch-file> --session <id>");
        return 1;
    }

    var patchPath = args[2];
    var sessionId = GetOption(args, "--session");
    if (sessionId is null)
    {
        Console.Error.WriteLine("Missing --session.");
        return 1;
    }

    var patchJson = await IOFile.ReadAllTextAsync(patchPath);
    var store = new SqliteStorageInitializer(connectionString);
    await store.InitializeAsync();
    var engine = new ProtocolEngine(new SearchService(store, store), store, store);
    using var document = JsonDocument.Parse(patchJson);
    var request = new ProtocolRequest("1.0", ProtocolOperations.Patch, sessionId, document.RootElement.Clone());
    var started = DateTimeOffset.UtcNow;
    var response = await engine.ExecuteAsync(request);
    await store.AddOperationMetricAsync(new OperationMetric("patch", started, DateTimeOffset.UtcNow - started, response.Success, sessionId));
    WriteResponse(response, format);
    return response.Success ? 0 : 1;
}

static async Task<int> PatchPreviewAsync(string connectionString, string[] args, string format)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: ecp patch preview <patch-file> --session <id>");
        return 1;
    }

    var patchPath = args[2];
    var sessionId = GetOption(args, "--session");
    if (sessionId is null)
    {
        Console.Error.WriteLine("Missing --session.");
        return 1;
    }

    var patchJson = await IOFile.ReadAllTextAsync(patchPath);
    var store = new SqliteStorageInitializer(connectionString);
    await store.InitializeAsync();
    var engine = new ProtocolEngine(new SearchService(store, store), store, store);
    using var document = JsonDocument.Parse(patchJson);
    var request = new ProtocolRequest("1.0", "PATCH_PREVIEW", sessionId, document.RootElement.Clone());
    var started = DateTimeOffset.UtcNow;
    var response = await engine.ExecuteAsync(request);
    await store.AddOperationMetricAsync(new OperationMetric("patch preview", started, DateTimeOffset.UtcNow - started, response.Success, sessionId));
    WriteResponse(response, format);
    return response.Success ? 0 : 1;
}

static async Task<int> PatchValidateAsync(string connectionString, string[] args, string format)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: ecp patch validate <patch-file> --session <id>");
        return 1;
    }

    var patchPath = args[2];
    var sessionId = GetOption(args, "--session");
    if (sessionId is null)
    {
        Console.Error.WriteLine("Missing --session.");
        return 1;
    }

    var patchJson = await IOFile.ReadAllTextAsync(patchPath);
    var store = new SqliteStorageInitializer(connectionString);
    await store.InitializeAsync();
    var engine = new ProtocolEngine(new SearchService(store, store), store, store);
    using var document = JsonDocument.Parse(patchJson);
    var request = new ProtocolRequest("1.0", "PATCH_VALIDATE", sessionId, document.RootElement.Clone());
    var started = DateTimeOffset.UtcNow;
    var response = await engine.ExecuteAsync(request);
    await store.AddOperationMetricAsync(new OperationMetric("patch validate", started, DateTimeOffset.UtcNow - started, response.Success, sessionId));
    WriteResponse(response, format);
    return response.Success ? 0 : 1;
}

static async Task<int> PatchRollbackAsync(string connectionString, string[] args, string format)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: ecp patch rollback <patch-file> --session <id>");
        return 1;
    }

    var patchPath = args[2];
    var sessionId = GetOption(args, "--session");
    if (sessionId is null)
    {
        Console.Error.WriteLine("Missing --session.");
        return 1;
    }

    var patchJson = await IOFile.ReadAllTextAsync(patchPath);
    var store = new SqliteStorageInitializer(connectionString);
    await store.InitializeAsync();
    var engine = new ProtocolEngine(new SearchService(store, store), store, store);
    using var document = JsonDocument.Parse(patchJson);
    var request = new ProtocolRequest("1.0", ProtocolOperations.PatchRollback, sessionId, document.RootElement.Clone());
    var started = DateTimeOffset.UtcNow;
    var response = await engine.ExecuteAsync(request);
    await store.AddOperationMetricAsync(new OperationMetric("patch rollback", started, DateTimeOffset.UtcNow - started, response.Success, sessionId));
    WriteResponse(response, format);
    return response.Success ? 0 : 1;
}

static async Task<int> RunBuildAsync(string connectionString, string[] args, string format)
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
    var request = new ProtocolRequest("1.0", ProtocolOperations.RunBuild, sessionId, JsonSerializer.Deserialize<JsonElement>("{}"));
    var started = DateTimeOffset.UtcNow;
    var response = await engine.ExecuteAsync(request);
    await store.AddOperationMetricAsync(new OperationMetric("build", started, DateTimeOffset.UtcNow - started, response.Success, sessionId));
    WriteResponse(response, format);
    return response.Success ? 0 : 1;
}

static async Task<int> RunTestsAsync(string connectionString, string[] args, string format)
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
    var request = new ProtocolRequest("1.0", ProtocolOperations.RunTests, sessionId, JsonSerializer.Deserialize<JsonElement>("{}"));
    var started = DateTimeOffset.UtcNow;
    var response = await engine.ExecuteAsync(request);
    await store.AddOperationMetricAsync(new OperationMetric("test", started, DateTimeOffset.UtcNow - started, response.Success, sessionId));
    WriteResponse(response, format);
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
    await store.AddOperationMetricAsync(new OperationMetric("diagnostics", started, DateTimeOffset.UtcNow - started, response.Success, sessionId));
    WriteResponse(response, format);
    return response.Success ? 0 : 1;
}

static async Task<int> SnapshotAsync(string connectionString, string[] args, string format)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: ecp snapshot <file> --session <id>");
        return 1;
    }

    var sessionId = GetOption(args, "--session");
    if (sessionId is null)
    {
        Console.Error.WriteLine("Missing --session.");
        return 1;
    }

    var store = new SqliteStorageInitializer(connectionString);
    var engine = new ProtocolEngine(new SearchService(store, store), store, store);
    var request = new ProtocolRequest("1.0", ProtocolOperations.GetFileSnapshot, sessionId, JsonSerializer.SerializeToElement(new { query = args[1] }));
    var started = DateTimeOffset.UtcNow;
    var response = await engine.ExecuteAsync(request);
    await store.AddOperationMetricAsync(new OperationMetric("snapshot", started, DateTimeOffset.UtcNow - started, response.Success, sessionId));
    WriteResponse(response, format);
    return response.Success ? 0 : 1;
}

static async Task<int> MethodAsync(string connectionString, string[] args, string format)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: ecp method <summary|get> <method> --session <id>");
        return 1;
    }

    var action = args[1];
    var sessionId = GetOption(args, "--session");
    if (sessionId is null)
    {
        Console.Error.WriteLine("Missing --session.");
        return 1;
    }

    var store = new SqliteStorageInitializer(connectionString);
    await store.InitializeAsync();
    var engine = new ProtocolEngine(new SearchService(store, store), store, store);
    var operation = action == "summary" ? ProtocolOperations.GetMethodSummary : action == "get" ? ProtocolOperations.GetMethod : null;
    if (operation is null)
    {
        Console.Error.WriteLine("Usage: ecp method <summary|get> <method> --session <id>");
        return 1;
    }

    var request = new ProtocolRequest("1.0", operation, sessionId, JsonSerializer.SerializeToElement(new { query = args[2] }));
    var started = DateTimeOffset.UtcNow;
    var response = await engine.ExecuteAsync(request);
    await store.AddOperationMetricAsync(new OperationMetric($"method {action}", started, DateTimeOffset.UtcNow - started, response.Success, sessionId));
    WriteResponse(response, format);
    return response.Success ? 0 : 1;
}

static async Task<int> TypeAsync(string connectionString, string[] args, string format)
{
    if (args.Length < 3 || args[1] != "get")
    {
        Console.Error.WriteLine("Usage: ecp type get <type> --session <id>");
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
    var request = new ProtocolRequest("1.0", ProtocolOperations.GetTypeOperation, sessionId, JsonSerializer.SerializeToElement(new { query = args[2] }));
    var started = DateTimeOffset.UtcNow;
    var response = await engine.ExecuteAsync(request);
    await store.AddOperationMetricAsync(new OperationMetric("type get", started, DateTimeOffset.UtcNow - started, response.Success, sessionId));
    WriteResponse(response, format);
    return response.Success ? 0 : 1;
}

static async Task<int> ReferencesAsync(string connectionString, string[] args, string format)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: ecp references <symbol> --session <id>");
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
    await store.AddOperationMetricAsync(new OperationMetric("get references", started, DateTimeOffset.UtcNow - started, response.Success, sessionId));
    WriteResponse(response, format);
    return response.Success ? 0 : 1;
}

static async Task<int> ReadAsync(string connectionString, string[] args, string format)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: ecp read <file|method|type> <target> --session <id> [--range start:end|--full]");
        return 1;
    }

    var action = args[1];
    var sessionId = GetOption(args, "--session");
    if (sessionId is null)
    {
        Console.Error.WriteLine("Missing --session.");
        return 1;
    }

    var store = new SqliteStorageInitializer(connectionString);
    await store.InitializeAsync();
    var engine = new ProtocolEngine(new SearchService(store, store), store, store);
    ProtocolRequest request;
    var started = DateTimeOffset.UtcNow;

    if (action == "file")
    {
        var full = HasFlag(args, "--full");
        var range = GetOption(args, "--range");
        if (!full && range is null)
        {
            Console.Error.WriteLine("Usage: ecp read file <file> --range start:end --session <id> | --full");
            return 1;
        }

        request = new ProtocolRequest("1.0", "READ_FILE", sessionId, JsonSerializer.SerializeToElement(new { query = args[2], range, full }));
    }
    else if (action == "method")
    {
        var id = GetOption(args, "--id");
        var query = GetOption(args, "--query");
        if (id is null && query is null)
        {
            query = args[2];
        }

        request = id is not null
            ? new ProtocolRequest("1.0", "READ_METHOD", sessionId, JsonSerializer.SerializeToElement(new { id }))
            : new ProtocolRequest("1.0", "READ_METHOD", sessionId, JsonSerializer.SerializeToElement(new { query }));
    }
    else if (action == "type")
    {
        var id = GetOption(args, "--id");
        var query = GetOption(args, "--query");
        if (id is null && query is null)
        {
            query = args[2];
        }

        request = id is not null
            ? new ProtocolRequest("1.0", "READ_TYPE", sessionId, JsonSerializer.SerializeToElement(new { id }))
            : new ProtocolRequest("1.0", "READ_TYPE", sessionId, JsonSerializer.SerializeToElement(new { query }));
    }
    else
    {
        Console.Error.WriteLine("Usage: ecp read <file|method|type> <target> --session <id> [--range start:end|--full]");
        return 1;
    }

    var response = await engine.ExecuteAsync(request);
    await store.AddOperationMetricAsync(new OperationMetric($"read {action}", started, DateTimeOffset.UtcNow - started, response.Success, sessionId));
    WriteResponse(response, format);
    return response.Success ? 0 : 1;
}

static bool HasFlag(string[] args, string name)
{
    foreach (var arg in args)
    {
        if (arg == name)
        {
            return true;
        }
    }

    return false;
}

static async Task<int> CallersAsync(string connectionString, string[] args, string format)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: ecp callers <method> --session <id>");
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
    var request = new ProtocolRequest("1.0", ProtocolOperations.GetCallers, sessionId, JsonSerializer.SerializeToElement(new { query = args[1] }));
    var started = DateTimeOffset.UtcNow;
    var response = await engine.ExecuteAsync(request);
    await store.AddOperationMetricAsync(new OperationMetric("get callers", started, DateTimeOffset.UtcNow - started, response.Success, sessionId));
    WriteResponse(response, format);
    return response.Success ? 0 : 1;
}

static async Task<int> CalleesAsync(string connectionString, string[] args, string format)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: ecp callees <method> --session <id>");
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
    await store.AddOperationMetricAsync(new OperationMetric("get callees", started, DateTimeOffset.UtcNow - started, response.Success, sessionId));
    WriteResponse(response, format);
    return response.Success ? 0 : 1;
}

static async Task<int> ReportAsync(string connectionString, string[] args, string format)
{
    var sessionId = GetOption(args, "--session");
    if (sessionId is null)
    {
        Console.Error.WriteLine("Missing --session.");
        return 1;
    }

    var store = new SqliteStorageInitializer(connectionString);
    await store.InitializeAsync();
    var session = await store.GetSessionAsync(new GetSessionRequest(new SessionId(Guid.Parse(sessionId))));
    if (session is null)
    {
        Console.Error.WriteLine("Session not found.");
        return 1;
    }

    var metrics = await store.GetOperationMetricsAsync(session.Id);
    var report = await new EcpReportComposer().ComposeAsync(session, metrics, store);

    if (format == "json")
    {
        Console.WriteLine(JsonSerializer.Serialize(report));
        return 0;
    }

    Console.WriteLine("Session Summary");
    Console.WriteLine($"Session: {session.Id.Value}");
    Console.WriteLine($"Repository: {session.RepositoryId.Value}");
    Console.WriteLine($"Searches: {session.SearchHistory.Count}");
    Console.WriteLine();
    Console.WriteLine("ECP Operation Log");
    foreach (var metric in report.OperationLog)
    {
        Console.WriteLine($"{metric.Operation} | {metric.Timestamp:O} | {metric.DurationMs}ms | {metric.Success}");
    }

    return 0;
}

static string? GetOption(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == name)
        {
            return args[i + 1];
        }
    }

    return null;
}

static void WriteResponse(ProtocolResponse response, string format)
{
    if (format == "json")
    {
        Console.WriteLine(JsonSerializer.Serialize(response));
        return;
    }

    if (response.Success)
    {
        Console.WriteLine(response.Data?.GetRawText() ?? "{}");
        return;
    }

    foreach (var error in response.Errors)
    {
        Console.Error.WriteLine($"{error.Code}: {error.Message}");
    }
}

static void PrintHelp()
{
    Console.WriteLine("""
        ecp index <path>
        ecp search symbol <query> --session <id>
        ecp search text <query> --session <id>
        ecp snapshot <file> --session <id>
        ecp session create
        """);
}
