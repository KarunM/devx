using System.Text.Json;
using System.Text.RegularExpressions;
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

            if (args.Length >= 2 && args[1] == "update")
            {
                return await IndexUpdateAsync(connectionString, args, format);
            }

            if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
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

        case "ask":
            return await AskAsync(connectionString, args, format);

        case "capabilities":
            return await CapabilitiesAsync(connectionString, args, format);

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

static async Task<int> IndexUpdateAsync(string connectionString, string[] args, string format)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: ecp index update <file> --session <id>");
        return 1;
    }

    var sessionId = GetOption(args, "--session");
    if (sessionId is null)
    {
        Console.Error.WriteLine("Missing --session.");
        return 1;
    }

    var filePath = args[2];
    var store = new SqliteStorageInitializer(connectionString);
    await store.InitializeAsync();
    var session = await store.GetSessionAsync(new GetSessionRequest(new SessionId(Guid.Parse(sessionId))));
    if (session is null)
    {
        Console.Error.WriteLine("Session not found.");
        return 1;
    }

    var root = await store.GetRepositoryRootPathAsync(session.RepositoryId);
    var repositoryRoot = root ?? Environment.CurrentDirectory;
    var indexer = new RepositoryIndexingService(store);
    var started = DateTimeOffset.UtcNow;
    await indexer.ReindexFileAsync(repositoryRoot, filePath);
    await store.AddOperationMetricAsync(new OperationMetric("index update", started, DateTimeOffset.UtcNow - started, true, sessionId));

    if (format == "json")
    {
        Console.WriteLine(JsonSerializer.Serialize(new { Success = true, File = filePath }));
    }
    else
    {
        Console.WriteLine("Updated file index.");
    }

    return 0;
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

    var patchPath = GetPatchPath(args);
    var sessionId = GetOption(args, "--session");
    if (sessionId is null)
    {
        Console.Error.WriteLine("Missing --session.");
        return 1;
    }

    if (patchPath is null)
    {
        Console.Error.WriteLine("Missing patch file.");
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

    var patchPath = GetPatchPath(args);
    var sessionId = GetOption(args, "--session");
    if (sessionId is null)
    {
        Console.Error.WriteLine("Missing --session.");
        return 1;
    }

    if (patchPath is null)
    {
        Console.Error.WriteLine("Missing patch file.");
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

    var patchPath = GetPatchPath(args);
    var sessionId = GetOption(args, "--session");
    if (sessionId is null)
    {
        Console.Error.WriteLine("Missing --session.");
        return 1;
    }

    if (patchPath is null)
    {
        Console.Error.WriteLine("Missing patch file.");
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

    var patchPath = GetPatchPath(args);
    var sessionId = GetOption(args, "--session");
    if (sessionId is null)
    {
        Console.Error.WriteLine("Missing --session.");
        return 1;
    }

    if (patchPath is null)
    {
        Console.Error.WriteLine("Missing patch file.");
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

static async Task<int> AskAsync(string connectionString, string[] args, string format)
{
    var useSandbox = args.Contains("--sandbox");
    var traceAsk = HasFlag(args, "--trace-ask") || HasFlag(args, "--debug-ask");

    var promptBuilder = new System.Text.StringBuilder();
    for (var i = 1; i < args.Length; i++)
    {
        if (args[i] is "--session" or "--format" or "--sandbox" || (i > 1 && args[i - 1] == "--session") || (i > 1 && args[i - 1] == "--format"))
        {
            if (args[i] is "--session" or "--format")
            {
                i++;
            }
            continue;
        }
        if (promptBuilder.Length > 0) promptBuilder.Append(' ');
        promptBuilder.Append(args[i]);
    }

    var userPrompt = promptBuilder.ToString().Trim();
    if (string.IsNullOrWhiteSpace(userPrompt))
    {
        Console.WriteLine("Enter your prompt (Ctrl+D to finish):");
        userPrompt = await Console.In.ReadToEndAsync();
    }

    if (string.IsNullOrWhiteSpace(userPrompt))
    {
        Console.Error.WriteLine("Prompt is required.");
        return 1;
    }

    var store = new SqliteStorageInitializer(connectionString);
    await store.InitializeAsync();
    var sessionResolution = await ResolveSessionAsync(connectionString, args, allowCreate: true, allowReuse: true);
    var session = sessionResolution.Session;
    if (session is null)
    {
        Console.Error.WriteLine(sessionResolution.ErrorMessage ?? "Session not found.");
        return 1;
    }

    var root = await store.GetRepositoryRootPathAsync(session.RepositoryId);
    var repositoryRoot = root ?? Environment.CurrentDirectory;
    var llmConfig = LoadLlmConfig(repositoryRoot) ?? new LlmConfig("mlx-lm", "mlx-community/Qwen3-8B-4bit", "http://localhost:8080", 0.7, 8192, 8192, 4000, 8);
    llmConfig = ApplyEnvOverrides(llmConfig);

    SandboxManager? sandbox = null;
    IRepositoryStore effectiveStore = store;
    string effectiveRoot = repositoryRoot;

    if (useSandbox)
    {
        sandbox = new SandboxManager(repositoryRoot);
        sandbox.CopySourceTree();
        effectiveStore = new SandboxRepositoryStore(store, sandbox.SandboxRoot);
        effectiveRoot = sandbox.SandboxRoot;
        Console.WriteLine($"Sandbox: {sandbox.SandboxRoot}");
    }

    var launchConfig = CreateServerLaunchConfig(llmConfig);
    using var serverManager = new LlmServerManager(llmConfig, launchConfig);
    try
    {
        await serverManager.EnsureRunningAsync();
    }
    catch (Exception ex)
    {
        WriteAskFailure(format, ex, serverManager.GetDiagnostics(), llmConfig.Endpoint);
        return 1;
    }

    using var llm = LlmClientFactory.Create(llmConfig);
    var engine = new ProtocolEngine(
        new SearchService(effectiveStore, (ISymbolStore)effectiveStore),
        (ISessionStore)effectiveStore,
        effectiveStore);
    var orchestrator = new AskOrchestrator(llm, engine, llmConfig, useSandbox ? sandbox?.SandboxRoot : null, traceAsk);
    var started = DateTimeOffset.UtcNow;

    Console.WriteLine("═══════════════════════════════════════════════════");
    Console.WriteLine("  ECP Ask — LLM-driven code orchestration");
    Console.WriteLine($"  Provider: {llmConfig.Provider} ({llmConfig.Model})");
    if (useSandbox) Console.WriteLine("  Mode: SANDBOX");
    Console.WriteLine("═══════════════════════════════════════════════════");
    Console.WriteLine();
    Console.WriteLine($">>> User: {userPrompt}");
    Console.WriteLine();

    var turnIndex = 0;
    orchestrator.OnTurn += (sender, turn) =>
    {
        turnIndex++;
        if (turn.Role == "assistant" && turn.ToolName == "done")
        {
            Console.WriteLine();
            Console.WriteLine("═══════════════════════════════════════════════════");
            Console.WriteLine($"  DONE: {turn.Content}");
            Console.WriteLine("═══════════════════════════════════════════════════");
        }
        else if (turn.Role == "assistant" && turn.ToolName is not null)
        {
            Console.WriteLine($"\n  [Turn {turnIndex}] 🤖 LLM → Tool call: {turn.ToolName}");
            var thoughtMatch = Regex.Match(turn.Content, @"Thought:\s*(.+?)(?:\n|$)", RegexOptions.Singleline);
            if (thoughtMatch.Success)
            {
                Console.WriteLine($"             💭 {thoughtMatch.Groups[1].Value.Trim()}");
            }
        }
        else if (turn.Role == "tool")
        {
            if (turn.ToolName == "patch_apply")
            {
                var success = turn.Content.Contains("\"Success\":true") || turn.Content.Contains("\"success\":true");
                var status = success ? "✅ PATCH APPLIED" : "❌ PATCH FAILED";
                Console.WriteLine($"  [Turn {turnIndex}] 🔧 ECP → {status}");
            }
            else if (turn.ToolName == "build")
            {
                var success = turn.Content.Contains("\"Success\":true") || turn.Content.Contains("\"success\":true");
                var status = success ? "✅ BUILD PASSED" : "❌ BUILD FAILED";
                Console.WriteLine($"  [Turn {turnIndex}] 🔧 ECP → {status}");
            }
            else if (turn.ToolName == "test")
            {
                var success = turn.Content.Contains("\"Success\":true") || turn.Content.Contains("\"success\":true");
                var status = success ? "✅ TESTS PASSED" : "❌ TESTS FAILED";
                Console.WriteLine($"  [Turn {turnIndex}] 🔧 ECP → {status}");
            }
            else
            {
                Console.WriteLine($"  [Turn {turnIndex}] 🔧 ECP → Result for {turn.ToolName} received");
            }
        }
        else if (turn.Role == "assistant")
        {
            var thoughtMatch = Regex.Match(turn.Content, @"Thought:\s*(.+?)(?:\n|$)", RegexOptions.Singleline);
            if (thoughtMatch.Success)
            {
                Console.WriteLine($"\n  [Turn {turnIndex}] 🤖 LLM → 💭 {thoughtMatch.Groups[1].Value.Trim()}");
            }
        }
    };

    AskResult askResult;
    try
    {
        askResult = await orchestrator.RunAsync(userPrompt, session);
    }
    catch (Exception ex)
    {
        WriteAskFailure(format, ex, serverManager.GetDiagnostics(), llmConfig.Endpoint);
        return 1;
    }
    await store.AddOperationMetricAsync(new OperationMetric("ask", started, DateTimeOffset.UtcNow - started, askResult.Success, session.Id.Value.ToString()));

    Console.WriteLine();
    Console.WriteLine($"Completed: {askResult.Summary}");

    if (useSandbox && sandbox is not null)
    {
        var changedFiles = sandbox.GetChangedFiles();
        if (changedFiles.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("═══════════════════════════════════════════════════");
            Console.WriteLine("  CHANGED FILES IN SANDBOX");
            Console.WriteLine("═══════════════════════════════════════════════════");
            foreach (var file in changedFiles)
            {
                Console.WriteLine($"  {file}");
            }

            Console.WriteLine();
            Console.WriteLine("Apply changes to real codebase? [y/N]: ");
            var response = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (response == "y" || response == "yes")
            {
                sandbox.ApplyChanges();
                Console.WriteLine("✅ Changes applied to real codebase.");
            }
            else
            {
                Console.WriteLine("❌ Changes discarded.");
            }
        }
        else
        {
            Console.WriteLine("No files were changed.");
        }
        sandbox.Dispose();
    }

    return askResult.Success ? 0 : 1;
}

static async Task<int> CapabilitiesAsync(string connectionString, string[] args, string format)
{
    var response = CapabilitiesComposer.Compose();

    if (format == "json")
    {
        Console.WriteLine(JsonSerializer.Serialize(response));
        return 0;
    }

    Console.WriteLine("ECP Capabilities");
    Console.WriteLine($"Version: {response.Version}");
    Console.WriteLine();
    Console.WriteLine("Global Options:");
    foreach (var option in response.GlobalOptions)
    {
        Console.WriteLine($"  --{option.Name}: {option.Description} (default: {option.Default}, required: {option.Required})");
    }
    Console.WriteLine();
    Console.WriteLine("Commands:");
    foreach (var command in response.Commands)
    {
        Console.WriteLine($"  {command.Name}: {command.Description}");
        Console.WriteLine($"    Usage: {command.Usage}");
        if (command.Subcommands.Length > 0)
        {
            Console.WriteLine($"    Subcommands: {string.Join(", ", command.Subcommands.Select(s => s.Name))}");
        }
        if (command.Args.Length > 0)
        {
            Console.WriteLine($"    Args: {string.Join(", ", command.Args)}");
        }
        if (command.Options.Length > 0)
        {
            Console.WriteLine($"    Options: {string.Join(", ", command.Options)}");
        }
    }
    return 0;
}

static LlmConfig? LoadLlmConfig(string repositoryRoot)
{
    var configPath = Path.Combine(repositoryRoot, ".ecp", "config.json");
    if (!IOFile.Exists(configPath))
    {
        return null;
    }

    try
    {
        var json = IOFile.ReadAllText(configPath);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("llm", out var llmElement))
        {
            return null;
        }

        var provider = llmElement.GetProperty("provider").GetString() ?? "ollama";
        var model = llmElement.GetProperty("model").GetString() ?? "qwen2.5-coder";
        var endpoint = llmElement.GetProperty("endpoint").GetString() ?? "http://localhost:11434";
        var temperature = 0.7;
        var maxTokens = 8192;
        var contextWindow = 8192;
        var maxToolResultLength = 4000;
        var conversationWindow = 8;
        if (llmElement.TryGetProperty("temperature", out var tempElement) && tempElement.ValueKind == JsonValueKind.Number)
        {
            temperature = tempElement.GetDouble();
        }
        if (llmElement.TryGetProperty("maxTokens", out var maxTokensElement) && maxTokensElement.ValueKind == JsonValueKind.Number)
        {
            maxTokens = maxTokensElement.GetInt32();
        }
        if (llmElement.TryGetProperty("contextWindow", out var cwElement) && cwElement.ValueKind == JsonValueKind.Number)
        {
            contextWindow = cwElement.GetInt32();
        }
        if (llmElement.TryGetProperty("maxToolResultLength", out var mtrlElement) && mtrlElement.ValueKind == JsonValueKind.Number)
        {
            maxToolResultLength = mtrlElement.GetInt32();
        }
        if (llmElement.TryGetProperty("conversationWindow", out var convwElement) && convwElement.ValueKind == JsonValueKind.Number)
        {
            conversationWindow = convwElement.GetInt32();
        }
        return new LlmConfig(provider, model, endpoint, temperature, maxTokens, contextWindow, maxToolResultLength, conversationWindow);
    }
    catch
    {
        return null;
    }
}

static LlmConfig ApplyEnvOverrides(LlmConfig config)
{
    var provider = Environment.GetEnvironmentVariable("OLLAMA_PROVIDER") ?? config.Provider;
    var model = Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? config.Model;
    var endpoint = Environment.GetEnvironmentVariable("OLLAMA_ENDPOINT") ?? config.Endpoint;
    var maxTokensStr = Environment.GetEnvironmentVariable("OLLAMA_MAX_TOKENS");
    var maxTokens = int.TryParse(maxTokensStr, out var parsed) ? parsed : config.MaxTokens;
    var contextWindowStr = Environment.GetEnvironmentVariable("OLLAMA_CONTEXT_WINDOW");
    var contextWindow = int.TryParse(contextWindowStr, out var cwParsed) ? cwParsed : config.ContextWindow;
    var maxToolResultLengthStr = Environment.GetEnvironmentVariable("OLLAMA_MAX_TOOL_RESULT_LENGTH");
    var maxToolResultLength = int.TryParse(maxToolResultLengthStr, out var mtrlParsed) ? mtrlParsed : config.MaxToolResultLength;
    var conversationWindowStr = Environment.GetEnvironmentVariable("OLLAMA_CONVERSATION_WINDOW");
    var conversationWindow = int.TryParse(conversationWindowStr, out var convwParsed) ? convwParsed : config.ConversationWindow;
    return config with { Provider = provider, Model = model, Endpoint = endpoint, MaxTokens = maxTokens, ContextWindow = contextWindow, MaxToolResultLength = maxToolResultLength, ConversationWindow = conversationWindow };
}

static LlmServerLaunchConfig CreateServerLaunchConfig(LlmConfig config)
{
    if (config.Provider.Equals("mlx", StringComparison.OrdinalIgnoreCase) || config.Provider.Equals("mlx-lm", StringComparison.OrdinalIgnoreCase))
    {
        return new LlmServerLaunchConfig(
            "mlx_lm.server",
            ["--model", "mlx-community/Qwen3-8B-4bit", "--port", "8080"],
            "v1/models",
            180,
            1000);
    }

    if (config.Provider.Equals("ollama", StringComparison.OrdinalIgnoreCase))
    {
        return new LlmServerLaunchConfig(
            "ollama",
            ["serve"],
            "api/tags",
            60,
            1000);
    }

    return new LlmServerLaunchConfig(string.Empty, Array.Empty<string>(), "v1/models", 1, 1000);
}

static void WriteAskFailure(string format, Exception exception, LlmServerStartupDiagnostics diagnostics, string endpoint, OpenAiMessage? assistantMessage = null, object? usage = null)
{
    if (format == "json")
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Success = false,
            Error = exception.Message,
            Diagnostics = diagnostics with { FinalFailureReason = exception.Message },
            AssistantMessage = assistantMessage,
            Usage = usage
        }));
        return;
    }

    Console.Error.WriteLine($"Failed to start or connect to the local LLM server: {exception.Message}");
    Console.Error.WriteLine($"Expected endpoint: {endpoint}");
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

static async Task<(SessionState? Session, string? ErrorMessage, bool Reused, bool Created)> ResolveSessionAsync(string connectionString, string[] args, bool allowCreate, bool allowReuse)
{
    var explicitSessionId = GetOption(args, "--session");
    var store = new SqliteStorageInitializer(connectionString);
    await store.InitializeAsync();

    if (!string.IsNullOrWhiteSpace(explicitSessionId))
    {
        var explicitSession = await store.GetSessionAsync(new GetSessionRequest(new SessionId(Guid.Parse(explicitSessionId))));
        return explicitSession is null
            ? (null, "Session not found.", false, false)
            : (explicitSession, null, false, false);
    }

    var repositoryRoot = Path.GetFullPath(Environment.CurrentDirectory);
    var repository = await store.GetRepositoryByRootPathAsync(repositoryRoot);
    if (repository is null)
    {
        return (null, "Repository has not been indexed yet.", false, false);
    }

    if (allowReuse)
    {
        var activeSession = await GetActiveSessionAsync(store, repository.Id);
        if (activeSession is not null)
        {
            Console.WriteLine($"Auto-reused active session: {activeSession.Id.Value}");
            return (activeSession, null, true, false);
        }
    }

    if (!allowCreate)
    {
        return (null, "No active session available.", false, false);
    }

    var created = await store.CreateSessionAsync(new CreateSessionRequest(repository.Id));
    await SetActiveSessionAsync(store, repository.Id, created.Id.Value.ToString());
    Console.WriteLine($"Auto-created active session: {created.Id.Value}");
    return (created, null, false, true);
}

static async Task<SessionState?> GetActiveSessionAsync(SqliteStorageInitializer store, RepositoryId repositoryId)
{
    var path = GetActiveSessionPath(repositoryId);
    if (!IOFile.Exists(path))
    {
        return null;
    }

    var sessionIdText = (await IOFile.ReadAllTextAsync(path)).Trim();
    if (string.IsNullOrWhiteSpace(sessionIdText) || !Guid.TryParse(sessionIdText, out var sessionGuid))
    {
        return null;
    }

    return await store.GetSessionAsync(new GetSessionRequest(new SessionId(sessionGuid)));
}

static async Task SetActiveSessionAsync(SqliteStorageInitializer store, RepositoryId repositoryId, string sessionId)
{
    var path = GetActiveSessionPath(repositoryId);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    await IOFile.WriteAllTextAsync(path, sessionId);
}

static string GetActiveSessionPath(RepositoryId repositoryId)
{
    return Path.Combine(".ecp", "current-session", repositoryId.Value.ToString(), "active");
}

static string? GetPatchPath(string[] args)
{
    for (var i = 2; i < args.Length; i++)
    {
        if (args[i].StartsWith("--", StringComparison.Ordinal))
        {
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                i++;
            }
            continue;
        }

        return args[i];
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
        ecp <command> [options]

        Commands:
          ecp ask <prompt> --session <id> [--sandbox]    Ask the LLM to orchestrate code changes
          ecp index <path>                              Index a repository
          ecp index status [--session <id>]               Check index status
          ecp index update <file> --session <id>          Re-index a single file
          ecp search symbol <query> --session <id>        Search symbols
          ecp search text <query> --session <id>          Search text
          ecp snapshot <file> --session <id>              Get file snapshot
          ecp session create                              Create a new session
          ecp patch <apply|preview|validate|rollback> <patch-file> --session <id>
                                                          Apply/manage patches
          ecp build --session <id>                      Build the solution
          ecp test --session <id>                       Run tests
          ecp diagnostics --session <id>                Get diagnostics
          ecp method <summary|get> <method> --session <id>
                                                          Get method info
          ecp type get <type> --session <id>            Get type info
          ecp callers <method> --session <id>            Get callers
          ecp callees <method> --session <id>            Get callees
          ecp references <symbol> --session <id>          Get references
          ecp read <file|method|type> <target> --session <id> [--range start:end|--full]
                                                          Read source
          ecp report --session <id>                     Generate session report
          ecp capabilities [--format json]                List all capabilities

        Global Options:
          --format <json|text>    Output format (default: text)
          --session <id>          Session identifier
          --sandbox               Run Ask in sandbox mode (changes not applied until approved)
        """);
}
