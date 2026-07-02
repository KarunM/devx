using System.Text.Json;
using Ecp.Domain;
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

        default:
            Console.Error.WriteLine($"Unknown command '{command}'.");
            return 1;
    }
}

static async Task IndexAsync(string connectionString, string repositoryPath)
{
    var store = new SqliteStorageInitializer(connectionString);
    var indexer = new RepositoryIndexingService(store);
    await indexer.IndexAsync(repositoryPath);
    Console.WriteLine("Indexed repository.");
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
    var engine = new ProtocolEngine(new SearchService(store, store), store, store);
    var request = new ProtocolRequest(
        "1.0",
        searchKind == "symbol" ? ProtocolOperations.SearchSymbol : ProtocolOperations.SearchText,
        sessionId,
        JsonSerializer.SerializeToElement(new { query }));

    var response = await engine.ExecuteAsync(request);
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
    var repositoryRoot = Path.GetFullPath(Environment.CurrentDirectory);
    var repository = new Repository(new RepositoryId(Guid.NewGuid()), Path.GetFileName(repositoryRoot), repositoryRoot);
    await store.InitializeAsync();
    await store.UpsertRepositoryAsync(repository);
    var session = await store.CreateSessionAsync(new CreateSessionRequest(repository.Id));
    WriteResponse(new ProtocolResponse(true, JsonSerializer.SerializeToElement(new { sessionId = session.Id.Value.ToString() }), [], []), format);
    return 0;
}

static async Task<int> PatchAsync(string connectionString, string[] args, string format)
{
    if (args.Length < 3 || args[1] != "apply")
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
    var engine = new ProtocolEngine(new SearchService(store, store), store, store);
    using var document = JsonDocument.Parse(patchJson);
    var request = new ProtocolRequest("1.0", ProtocolOperations.Patch, sessionId, document.RootElement.Clone());
    var response = await engine.ExecuteAsync(request);
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
    var engine = new ProtocolEngine(new SearchService(store, store), store, store);
    var request = new ProtocolRequest("1.0", ProtocolOperations.RunBuild, sessionId, JsonSerializer.Deserialize<JsonElement>("{}"));
    var response = await engine.ExecuteAsync(request);
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
    var engine = new ProtocolEngine(new SearchService(store, store), store, store);
    var request = new ProtocolRequest("1.0", ProtocolOperations.RunTests, sessionId, JsonSerializer.Deserialize<JsonElement>("{}"));
    var response = await engine.ExecuteAsync(request);
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
    var engine = new ProtocolEngine(new SearchService(store, store), store, store);
    var request = new ProtocolRequest("1.0", ProtocolOperations.GetDiagnostics, sessionId, JsonSerializer.Deserialize<JsonElement>("{}"));
    var response = await engine.ExecuteAsync(request);
    WriteResponse(response, format);
    return response.Success ? 0 : 1;
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
        ecp session create
        """);
}
