using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Reflection;
using DevX.Application;
using DevX.Domain;
using DevX.Cli;
using DevX.Infrastructure.Storage.SQLite;
using DevX.Indexer;
using DevX.Protocol;
using Microsoft.Data.Sqlite;
using IOFile = System.IO.File;

var exitCode = await RunAsync(args);
return exitCode;

static async Task<int> RunAsync(string[] args)
{
    OperationContext.TaskLabel.Value = GetOption(args, "--task");
    var helpCommand = "";
    if (args.Length == 0 || WantsHelp(args, out helpCommand))
    {
        // An agent asking for help wants the ten commands, not the manual: 17 of the 64
        // unexplained calls in the v2 arm-B transcripts were `devx --help`.
        //
        // 1.6.10 regression: this only looked at args[0], so the RULES_B-style invocation
        // with the global flags first (`devx --session x --agent codex --task v1 --help`)
        // fell through to the dispatcher and answered "Unknown command '--session'".
        // Help is now recognised wherever it sits among the leading options.
        //
        // `devx patch create --help` must reach the ops list, not the manual: an agent that
        // never saw the op names invented them (see PatchOperationCatalog).
        if (helpCommand is "patch create" or "patch") Console.WriteLine(PatchOperationCatalog.HelpText());
        else if (args.Contains("--agent", StringComparer.Ordinal)) PrintAgentHelp();
        else PrintHelp();
        return 0;
    }

    if (args[0] is "--version" or "version")
    {
        PrintVersion(GetOption(args, "--format") ?? "text");
        return 0;
    }

    if (args[0] == "init")
    {
        return await InitAsync(args, GetOption(args, "--format") ?? "text");
    }

    if (!args.Contains("--session", StringComparer.Ordinal) && args[0] is ("index" or "search" or "read" or "method" or "type" or "callers" or "callees" or "references" or "snapshot" or "test" or "build" or "run" or "replace" or "patch"))
    {
        args = await AddAutomaticSessionAsync(args);
    }
    if (args[0] is "search" or "read" or "snapshot" or "callers" or "callees" or "references" or "method" or "type")
    {
        await EnsureAutomaticIndexAsync();
    }

    var format = ResolveFormat(args);
    var command = args[0];
    var databasePath = ResolveDatabasePath();
    var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();

    switch (command)
    {
        case "index":
            if (args.Length >= 2 && args[1] == "status")
            {
                return await IndexStatusAsync(connectionString, args, format);
            }

            if (args.Length >= 2 && args[1] == "changed")
            {
                return await IndexChangedAsync(connectionString, args, format);
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

            await IndexAsync(connectionString, args[1], args, GetAgentLabel(args));
            return 0;

        case "search":
            return await SearchAsync(connectionString, args, format);

        case "session":
            return await SessionAsync(connectionString, args, format);

        case "patch":
            return await PatchAsync(connectionString, args, format);

        case "replace":
            {
                var replaceStore = new SqliteStorageInitializer(connectionString);
                await replaceStore.InitializeAsync();
                var replaceSearch = new SearchService(replaceStore, replaceStore);
                var replaceIndexer = new RepositoryIndexingService(replaceStore);
                return await ReplaceCommand.RunAsync(args, Environment.CurrentDirectory, root =>
                {
                    var runner = new DotnetBuildTestService();
                    return new VerifyAfterEdit(
                        file => VerifyAfterEdit.RunBuildAsync(runner, root, file),
                        files => runner.RunTestsAsync(root, files, TimeSpan.FromSeconds(300)),
                        file => VerifyAfterEdit.FindRelevantTestFiles(root, file, replaceStore, replaceSearch));
                },
                // Re-indexes the edited file by default; `--no-index` opts out (brief OSS12 item 2).
                args.Contains("--no-index", StringComparer.Ordinal)
                    ? null
                    : async file => { await replaceIndexer.ReindexFilesAsync(Environment.CurrentDirectory, [file]); });
            }

        case "health":
            return await HealthAsync(connectionString, args, format);

        case "doctor":
            return await DoctorAsync(connectionString, args, format);

        case "artifact":
            return await ArtifactAsync(args, format);

        case "build":
            return await RunBuildAsync(connectionString, args, format);

        case "test":
            return await RunTestsAsync(connectionString, args, format);

        case "diagnostics":
            return await DiagnosticsAsync(connectionString, args, format);

        case "run":
            return await RunToolAsync(connectionString, args, format);

        case "fallback":
            return await FallbackAsync(connectionString, args, format);

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

        case "mcp":
            if (args.Length < 2 || args[1] != "serve")
            {
                Console.Error.WriteLine("Usage: devx mcp serve");
                return 1;
            }
            return await McpServeAsync(connectionString);

        default:
            Console.Error.WriteLine($"Unknown command '{command}'."
                + (ClosestCommand(command) is { } near ? $" Did you mean '{near}'?" : " Run 'devx --help --agent' for the command list."));
            return 1;
    }
}

// Options whose next token is their VALUE, not a command. `devx --session <id> --help`
// must not read the session id as a command name.
static bool HelpScanValueOption(string token) => token switch
{
    "--session" or "--agent" or "--task" or "--format" or "--op" or "--path"
        or "--content" or "--content-file" or "--replacement" or "--replacement-file"
        or "--method" or "--method-text" or "--method-text-file" or "--position"
        or "--namespace" or "--containing-type" or "--type-id" or "--occurrence"
        or "--old" or "--old-text" or "--new" or "--output" or "--input" or "--period"
        or "--blocked-terms" or "--runtime" or "--target" or "--timeout" or "--org"
        or "--team" or "--repo" or "--name" or "--capability" or "--failed-command"
        or "--reason" or "--action" or "--command" or "--file" or "--coverage-start"
        or "--coverage-end" or "--range" => true,
    _ => false
};

// The two-word command forms, so `devx patch create --help` reaches the patch help while
// `devx search Foo --help` is left alone (Foo is a query, not a subcommand).
static bool TwoWordCommand(string two) => two switch
{
    "patch create" or "patch preview" or "patch validate" or "patch apply" or "patch rollback"
        or "index status" or "index update" or "index changed" or "artifact create"
        or "artifact delete" or "artifact path" or "search symbol" or "search text"
        or "read method" or "read type" or "read section" or "report html" or "report export"
        or "fallback record" or "fallback observe" or "session create" or "session list" => true,
    _ => false
};

// --help has to work wherever it sits among the leading options -- RULES_B tells agents to
// pass --session/--agent/--task on every call, and on 1.6.10 that turned `devx --help` into
// "Unknown command '--session'". It must still NOT hijack a prompt that merely mentions it
// (`devx ask "explain --help"`), so the scan stops as soon as real argument text begins.
static bool WantsHelp(string[] args, out string command)
{
    command = "";
    var words = 0;
    for (var i = 0; i < args.Length; i++)
    {
        var token = args[i];
        if (token is "--help" or "-h")
        {
            return true;
        }
        if (token.StartsWith('-'))
        {
            if (HelpScanValueOption(token))
            {
                i++;
            }
            continue;
        }
        words++;
        if (words == 1)
        {
            command = token;
            continue;
        }
        if (words == 2 && TwoWordCommand($"{command} {token}"))
        {
            command = $"{command} {token}";
            continue;
        }
        return false;
    }
    return false;
}

// The commands an agent actually reaches for. Used only to turn a typo into a fix.
static string? ClosestCommand(string command)
{
    string[] KnownCommands =
    {
        "init", "index", "search", "read", "snapshot", "session", "capabilities",
        "test", "build", "patch", "replace", "mcp", "ask", "report", "version"
    };
    string? best = null;
    var bestDistance = int.MaxValue;
    foreach (var candidate in KnownCommands)
    {
        var distance = Levenshtein(command, candidate);
        if (distance < bestDistance) { bestDistance = distance; best = candidate; }
    }
    // Only suggest an actual neighbour; "frobnicate" should get the help line instead.
    return bestDistance <= Math.Max(2, command.Length / 3) ? best : null;
}

static int Levenshtein(string a, string b)
{
    var previous = new int[b.Length + 1];
    var current = new int[b.Length + 1];
    for (var j = 0; j <= b.Length; j++) previous[j] = j;
    for (var i = 1; i <= a.Length; i++)
    {
        current[0] = i;
        for (var j = 1; j <= b.Length; j++)
            current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1),
                previous[j - 1] + (char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1));
        (previous, current) = (current, previous);
    }
    return previous[b.Length];
}

// Deliberately short: this is the whole point of it existing next to PrintHelp. The edit
// line is the one that matters -- on 1.6.10 it named only `patch`, so agents guessed at op
// names instead of using `devx replace`, which is what they wanted.
static void PrintAgentHelp()
{
    Console.WriteLine("devx cheat sheet (add --session <id>; piped output is already agent format)");
    Console.WriteLine("  --format json still gives the full JSON shape; --format text gives the human text");
    Console.WriteLine("  devx index .                      index the repo (or: index update <files>)");
    Console.WriteLine("  devx search symbol <name>         find it; one hit returns its source too");
    Console.WriteLine("  devx search text <literal>        literal text search");
    Console.WriteLine("  devx read method \"<Qualified>\"    read ONE function; \"<file#Heading>\" for a doc section");
    Console.WriteLine("  devx read <Name> <Name>          several symbols in ONE call, not one call each");
    Console.WriteLine("  devx snapshot <file>              map a file: classes + functions, no bodies");
    Console.WriteLine("  EDIT -- pick one:");
    Console.WriteLine("  devx replace <file> --old <text> --new <text>   one text edit (--all | --occurrence n)");
    Console.WriteLine("  devx patch create|validate|apply  whole-function edits by name (--op; see --help)");
    Console.WriteLine("  devx test [<files>]               run tests; exit 0 = pass");
    Console.WriteLine("  devx capabilities                 what this build can do");
    if (IsAskConfigured(Environment.CurrentDirectory))
    {
        Console.WriteLine("  devx ask \"<question>\"             ask the configured LLM about this repo");
    }
}

static async Task<int> McpServeAsync(string connectionString)
{
    var store = new SqliteStorageInitializer(connectionString);
    await store.InitializeAsync();
    var root = FindRepositoryRoot(Environment.CurrentDirectory);
    var repository = new Repository(new RepositoryId(StableId(root)), Path.GetFileName(root), root);
    await store.UpsertRepositoryAsync(repository);
    var session = await AutomaticSessionResolver.GetOrCreateAsync(store, root, repository);
    var engine = new ProtocolEngine(new SearchService(store, store), store, store);
    var indexer = new RepositoryIndexingService(store);
    var reference = new McpReferenceServer(
        engine,
        store,
        store,
        session.Id.Value.ToString(),
        (files, cancellationToken) => indexer.ReindexFilesAsync(root, files, cancellationToken),
        GetProductVersion(),
        cancellationToken => indexer.IndexAsync(root, cancellationToken),
        cancellationToken => store.InitializeAsync(cancellationToken),
        // Left null so the server's start-of-session refresh and its "already current" answer
        // degrade to indexing rather than failing when git cannot report changed files.
        changedFilesAsync: indexer.GetChangedFilesAsync);
    await new McpStdioServer(reference).RunAsync(Console.In, Console.Out);
    return 0;
}

static async Task<int> InitAsync(string[] args, string format)
{
    var agents = (GetOption(args, "--agents") ?? "claude,codex")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    try
    {
        var updates = await AgentRulesInitializer.InitializeAsync(
            Environment.CurrentDirectory,
            agents,
            args.Contains("--global", StringComparer.Ordinal));
        if (format == "json")
        {
            Console.WriteLine(JsonSerializer.Serialize(new { Success = true, Scope = args.Contains("--global", StringComparer.Ordinal) ? "global" : "repository", Updates = updates }));
        }
        else
        {
            foreach (var update in updates)
            {
                Console.WriteLine($"{(update.ReplacedExistingBlock ? "Updated" : "Added")} DevX rules for {update.Agent}: {update.Path}");
            }
        }
        return 0;
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
    {
        Console.Error.WriteLine(exception.Message);
        return 1;
    }
}

static async Task IndexAsync(string connectionString, string repositoryPath, string[] args, string? agentLabel)
{
    var started = DateTimeOffset.UtcNow;
    var store = new SqliteStorageInitializer(connectionString);
    await store.InitializeAsync();
    var indexer = new RepositoryIndexingService(store);

    // `devx index .` on an untouched checkout used to walk and re-parse every file: on Django
    // that is 3,292 files and over 13 minutes of work to arrive at the index that was already
    // on disk. And on a checkout with one edited file it did the same 13 minutes, because the
    // CLI had no notion of a changed file at all. The MCP server got both answers; the CLI --
    // the way the benchmark drives it -- did not, which is the call that looked like a hang.
    if (!args.Contains("--force", StringComparer.Ordinal) && await CurrentIndexAsync(indexer, repositoryPath) is { } answer)
    {
        await store.AddOperationMetricAsync(new OperationMetric("index", started, DateTimeOffset.UtcNow - started, true, null, AgentLabel: agentLabel));
        Console.WriteLine(answer);
        return;
    }

    var result = await indexer.IndexAsync(repositoryPath);
    await store.AddOperationMetricAsync(new OperationMetric("index", started, DateTimeOffset.UtcNow - started, true, null, AgentLabel: agentLabel));
    Console.WriteLine(result.Reason is null
        ? $"Indexed {result.FilesIndexed} files."
        : $"Indexed {result.FilesIndexed} files. Reason: {result.Reason}.");
}

/// <summary>
/// What `devx index .` should say instead of doing the full run, or null when the full run is
/// genuinely needed. Git being unavailable (a plain folder, no repository) means DevX cannot
/// tell what changed, and the answer to "cannot tell" is to do the work.
/// </summary>
static async Task<string?> CurrentIndexAsync(RepositoryIndexingService indexer, string repositoryPath)
{
    IReadOnlyList<string> changed;
    try
    {
        changed = await indexer.GetChangedFilesAsync(repositoryPath);
    }
    catch (Exception exception) when (exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
    {
        return null;
    }

    var indexed = await indexer.CountIndexedFilesAsync(repositoryPath);

    // Nothing is indexed yet, so there is nothing to bring up to date file by file.
    if (indexed == 0) return null;
    if (changed.Count == 0) return $"already current ({indexed} files).";

    // A repository that is already indexed and has a handful of edited files is the common
    // case an agent is in, and re-parsing its whole tree for those files is the expensive
    // mistake. Only the files git names are re-indexed.
    var refreshed = await indexer.ReindexChangedFilesAsync(repositoryPath);
    return $"Indexed {refreshed.UpdatedFiles} files (re-indexed the {changed.Count} files git reports as changed, out of {indexed}).";
}

static async Task<int> IndexUpdateAsync(string connectionString, string[] args, string format)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: devx index update <file> [<file> ...] --session <id>");
        return 1;
    }

    var sessionId = GetOption(args, "--session");
    if (sessionId is null)
    {
        Console.Error.WriteLine("Missing --session.");
        return 1;
    }

    // Drop every flag AND the value that follows it. The previous predicate compared each arg to
    // its predecessor within the same skipped slice but still admitted a flag value whenever the
    // flag sat at the slice boundary, so `--session <guid>` leaked the GUID through as a file path
    // and every run printed a bogus "SKIPPED: <guid> — Unsupported file type ''".
    var positional = args.Skip(2).ToArray();
    var filePaths = new List<string>();
    for (var i = 0; i < positional.Length; i++)
    {
        if (positional[i].StartsWith("--", StringComparison.Ordinal))
        {
            // Flags that take a value consume the next token; boolean flags do not.
            if (i + 1 < positional.Length && !positional[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                i++;
            }

            continue;
        }

        filePaths.Add(positional[i]);
    }

    if (filePaths.Count == 0)
    {
        Console.Error.WriteLine("Missing file path.");
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

    var root = await store.GetRepositoryRootPathAsync(session.RepositoryId);
    var repositoryRoot = root ?? Environment.CurrentDirectory;
    var indexer = new RepositoryIndexingService(store);
    var started = DateTimeOffset.UtcNow;
    var result = await indexer.ReindexFilesAsync(repositoryRoot, filePaths.ToArray());
    var updateSucceeded = result.UpdatedFiles > 0 || !result.SkippedFilePaths.Any(file => file.Reason.StartsWith("ERROR:", StringComparison.Ordinal));
    await store.AddOperationMetricAsync(new OperationMetric("index update", started, DateTimeOffset.UtcNow - started, updateSucceeded, sessionId,
        JsonSerializer.Serialize(new { UnsupportedFilesSkipped = result.SkippedFilePaths.Count(f => f.Reason.StartsWith("Unsupported file type", StringComparison.Ordinal)) }), GetAgentLabel(args)));

    if (format == "json")
    {
        Console.WriteLine(JsonSerializer.Serialize(new { Success = updateSucceeded, result.Mode, result.UpdatedFiles, result.SkippedFiles, result.RequestedFiles, result.PerformedFullReindex, result.SkippedFilePaths }));
    }
    else
    {
        Console.WriteLine(result.PerformedFullReindex
            ? "Performed full repository re-index due to structural file changes."
            : $"Updated file index. {result.UpdatedFiles} updated, {result.SkippedFiles} skipped.");

        foreach (var skipped in result.SkippedFilePaths)
        {
            Console.WriteLine($"  SKIPPED: {skipped.Path} — {skipped.Reason}");
        }
    }

    return updateSucceeded ? 0 : 1;
}

static async Task<int> IndexChangedAsync(string connectionString, string[] args, string format)
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

    var root = await store.GetRepositoryRootPathAsync(session.RepositoryId);
    var repositoryRoot = root ?? Environment.CurrentDirectory;
    var indexer = new RepositoryIndexingService(store);
    var started = DateTimeOffset.UtcNow;
    var result = await indexer.ReindexChangedFilesAsync(repositoryRoot);
    await store.AddOperationMetricAsync(new OperationMetric("index changed", started, DateTimeOffset.UtcNow - started, true, sessionId, AgentLabel: GetAgentLabel(args)));

    if (format == "json")
    {
        Console.WriteLine(JsonSerializer.Serialize(new { Success = true, result.Mode, result.UpdatedFiles, result.SkippedFiles, result.RequestedFiles, result.PerformedFullReindex }));
    }
    else
    {
        Console.WriteLine(result.RequestedFiles.Count == 0
            ? "No changed files required re-indexing."
            : result.PerformedFullReindex
                ? "Changed structural files detected; performed full repository re-index."
                : "Updated changed file indexes.");
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
        await store.AddOperationMetricAsync(new OperationMetric("index status", started, DateTimeOffset.UtcNow - started, true, sessionId, AgentLabel: GetAgentLabel(args)));
    }

    WriteResponse(new ProtocolResponse(true, JsonSerializer.SerializeToElement(response), [], []), format);
    return 0;
}

static async Task<int> SearchAsync(string connectionString, string[] args, string format)
{
    var implicitSymbol = args.Length >= 2 && args[1] is not ("symbol" or "text") && !args[1].StartsWith("--", StringComparison.Ordinal);
    if (args.Length < (implicitSymbol ? 2 : 4))
    {
        Console.Error.WriteLine("Usage: devx search <query> [--session <id>] | devx search <symbol|text> <query> [--session <id>]");
        return 1;
    }

    var searchKind = implicitSymbol ? "symbol" : args[1];
    // Several queries in one call: `devx search text "a" "b"` is the alternation `a|b`, so a
    // guess that missed costs nothing instead of another round trip. In the 1.6.11 transcripts
    // 34 of the 40 chained `search text` calls were a different word tried one at a time.
    var queryParts = PositionalQueryParts(args, implicitSymbol ? 1 : 2);
    if (queryParts.Count == 0)
    {
        Console.Error.WriteLine("Usage: devx search <query> [--session <id>] | devx search <symbol|text> <query> [<query> ...] [--session <id>]");
        return 1;
    }

    var query = searchKind == "text" ? string.Join('|', queryParts) : queryParts[0];
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
    if (implicitSymbol && response.Success && response.Data is { ValueKind: JsonValueKind.Object } data && TryGetPropertyIgnoreCase(data, "Symbols", out var matches) && matches.ValueKind == JsonValueKind.Array && matches.GetArrayLength() == 0)
    {
        Console.Error.WriteLine("(no symbol match; showing text matches)");
        response = await engine.ExecuteAsync(new ProtocolRequest("1.0", ProtocolOperations.SearchText, sessionId, JsonSerializer.SerializeToElement(new { query })));
        searchKind = "text";
    }
    // Search and read in one call. Only for a SINGLE hit: several equal hits must always be
    // listed, never silently resolved to one. The hit line is printed first, so the agent
    // keeps the identity (qualified name, path, span) as well as the source. Agent format
    // only -- --format json keeps its exact shape.
    string? autoRead = null;
    if (format == "agent" && searchKind == "symbol" && response.Success && response.Data is { ValueKind: JsonValueKind.Object } searchData && TryGetPropertyIgnoreCase(searchData, "Symbols", out var singleHits) && singleHits.ValueKind == JsonValueKind.Array && singleHits.GetArrayLength() == 1)
    {
        var hit = singleHits[0];
        var id = JsonPropertyText(hit, "Id");
        var kind = JsonPropertyText(hit, "Kind");
        if (id.Length > 0)
        {
            var operation = kind is "Class" or "Type" or "Record" or "Interface" or "Struct" or "Enum" ? "READ_TYPE" : "READ_METHOD";
            var read = await engine.ExecuteAsync(new ProtocolRequest("1.0", operation, sessionId, JsonSerializer.SerializeToElement(new { id })));
            if (read.Success) autoRead = AutoReadText(read);
        }
    }

    // Every content hit is named for the symbol it sits in -- the one thing a plain `rg -n`
    // cannot say, and what makes the next call a `read <symbol>` instead of a guess.
    IReadOnlyDictionary<(string FileId, int Line), string>? enclosingSymbols = null;
    if (format == "agent" && searchKind == "text" && response.Success && response.Data is { ValueKind: JsonValueKind.Object } hitData && TryGetPropertyIgnoreCase(hitData, "ContentMatches", out var contentHits) && contentHits.ValueKind == JsonValueKind.Array)
    {
        var points = contentHits.EnumerateArray()
            .Take(10)
            .Select(item => (FileId: JsonPropertyText(item, "FileId"), Line: int.TryParse(JsonPropertyText(item, "Line"), out var at) ? at : 0))
            .Where(point => point.FileId.Length > 0 && point.Line > 0)
            .ToList();
        if (points.Count > 0)
        {
            enclosingSymbols = await store.FindEnclosingSymbolsAsync(points);
        }
    }

    await store.AddOperationMetricAsync(new OperationMetric($"search {searchKind}", started, DateTimeOffset.UtcNow - started, response.Success, sessionId, OperationMetadata(response, query), GetAgentLabel(args)));
    WriteResponse(response, format, enclosingSymbols);
    if (autoRead is not null) Console.WriteLine(autoRead);
    return response.Success ? 0 : 1;
}

static List<string> PositionalQueryParts(string[] args, int start)
{
    // The options `search` accepts that take a value, so their value is never mistaken for a
    // query. Everything else that starts with `-` is a flag.
    string[] valueOptions = ["--session", "--agent", "--task", "--format"];
    var parts = new List<string>();
    for (var i = start; i < args.Length; i++)
    {
        if (args[i].StartsWith('-'))
        {
            if (valueOptions.Contains(args[i], StringComparer.Ordinal))
            {
                i++;                       // skip the option's value
            }

            continue;
        }

        parts.Add(args[i]);
    }

    return parts;
}

static async Task<int> SessionAsync(string connectionString, string[] args, string format)
{
    if (args.Length < 2 || args[1] != "create")
    {
        Console.Error.WriteLine("Usage: devx session create");
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
    await store.AddOperationMetricAsync(new OperationMetric("session create", started, DateTimeOffset.UtcNow - started, true, session.Id.Value.ToString(), AgentLabel: GetAgentLabel(args)));
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

static async Task<string[]> AddAutomaticSessionAsync(string[] args)
{
    var connectionString = new SqliteConnectionStringBuilder { DataSource = ResolveDatabasePath() }.ToString();
    var store = new SqliteStorageInitializer(connectionString);
    await store.InitializeAsync();
    var root = FindRepositoryRoot(Environment.CurrentDirectory);
    var repository = new Repository(new RepositoryId(StableId(root)), Path.GetFileName(root), root);
    var session = await AutomaticSessionResolver.GetOrCreateAsync(store, root, repository);
    return [.. args, "--session", session.Id.Value.ToString()];
}

static string FindRepositoryRoot(string path)
{
    var root = Path.GetFullPath(path);
    for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
    {
        if (Directory.Exists(Path.Combine(directory.FullName, ".devx")) || Directory.Exists(Path.Combine(directory.FullName, ".git")) || IOFile.Exists(Path.Combine(directory.FullName, ".git")))
            return directory.FullName;
    }
    return root;
}

static async Task EnsureAutomaticIndexAsync()
{
    var connectionString = new SqliteConnectionStringBuilder { DataSource = ResolveDatabasePath() }.ToString();
    var store = new SqliteStorageInitializer(connectionString);
    await store.InitializeAsync();
    var root = Path.GetFullPath(Environment.CurrentDirectory);
    for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
    {
        if (Directory.Exists(Path.Combine(directory.FullName, ".devx")) || Directory.Exists(Path.Combine(directory.FullName, ".git")) || IOFile.Exists(Path.Combine(directory.FullName, ".git")))
        {
            root = directory.FullName;
            break;
        }
    }
    var repository = await store.GetRepositoryByRootPathAsync(root);
    if (repository is not null && await store.CountIndexedFilesAsync(repository.Id) > 0)
        return;
    var result = await new RepositoryIndexingService(store).IndexAsync(root);
    Console.Error.WriteLine($"(indexed {result.FilesIndexed} files)");
}

static async Task<int> PatchAsync(string connectionString, string[] args, string format)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: devx patch <create|apply|preview|validate|rollback> <patch-file> --session <id>");
        return 1;
    }

    if (args[1] == "create")
    {
        return await PatchCreateAsync(connectionString, args, format);
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
        Console.Error.WriteLine("Usage: devx patch apply <patch-file> --session <id>");
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
    var engine = WriteEngine(store, args);
    using var document = JsonDocument.Parse(patchJson);
    var request = new ProtocolRequest("1.0", ProtocolOperations.Patch, sessionId, document.RootElement.Clone());
    var started = DateTimeOffset.UtcNow;
    var response = await engine.ExecuteAsync(request);
    await store.AddOperationMetricAsync(new OperationMetric("patch", started, DateTimeOffset.UtcNow - started, response.Success, sessionId, OperationMetadata(response, patchPath), GetAgentLabel(args)));
    WriteResponse(response, format);
    return response.Success ? 0 : 1;
}

static async Task<int> ArtifactAsync(string[] args, string format)
    => await DevX.Cli.ArtifactCommand.RunAsync(args.Skip(1).ToArray(), format);

// Brief OSS12 item 2: a write re-indexes the files it touched, by default, on every path --
// replace, patch apply, patch rollback, patch create/delete, and the MCP edits that go through
// the same ProtocolEngine. `--no-index` is the opt-out. Agents used to be told to run
// `devx index update` after each edit; 43 such calls in one series of 39 runs.
static ProtocolEngine WriteEngine(SqliteStorageInitializer store, string[] args)
    => new(new SearchService(store, store), store, store, new DotnetBuildTestService(), new PatchLifecycleService(),
        refreshIndexAfterEdit: !args.Contains("--no-index", StringComparer.Ordinal));

static async Task<int> PatchCreateAsync(string connectionString, string[] args, string format)
    => await DevX.Cli.PatchCreateCommand.RunAsync(connectionString, args, format);

static async Task<int> PatchPreviewAsync(string connectionString, string[] args, string format)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: devx patch preview <patch-file> --session <id>");
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
    await store.AddOperationMetricAsync(new OperationMetric("patch preview", started, DateTimeOffset.UtcNow - started, response.Success, sessionId, OperationMetadata(response, patchPath), GetAgentLabel(args)));
    WriteResponse(response, format);
    return response.Success ? 0 : 1;
}

static async Task<int> PatchValidateAsync(string connectionString, string[] args, string format)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: devx patch validate <patch-file> --session <id>");
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
    await store.AddOperationMetricAsync(new OperationMetric("patch validate", started, DateTimeOffset.UtcNow - started, response.Success, sessionId, OperationMetadata(response, patchPath), GetAgentLabel(args)));
    WriteResponse(response, format);
    return response.Success ? 0 : 1;
}

static async Task<int> PatchRollbackAsync(string connectionString, string[] args, string format)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: devx patch rollback <patch-file> --session <id>");
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
    var engine = WriteEngine(store, args);
    using var document = JsonDocument.Parse(patchJson);
    var request = new ProtocolRequest("1.0", ProtocolOperations.PatchRollback, sessionId, document.RootElement.Clone());
    var started = DateTimeOffset.UtcNow;
    var response = await engine.ExecuteAsync(request);
    await store.AddOperationMetricAsync(new OperationMetric("patch rollback", started, DateTimeOffset.UtcNow - started, response.Success, sessionId, OperationMetadata(response, patchPath), GetAgentLabel(args)));
    WriteResponse(response, format);
    return response.Success ? 0 : 1;
}

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

    if (format == "json")
    {
        Console.WriteLine(JsonSerializer.Serialize(doctor));
        return doctor.Health.Status == "Unavailable" ? 1 : 0;
    }

    Console.WriteLine("DevX Doctor");
    Console.WriteLine($"Status: {doctor.Health.Status}");
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

static async Task<int> SnapshotAsync(string connectionString, string[] args, string format)
{
    var target = args.Skip(1).FirstOrDefault(argument => !argument.StartsWith("--", StringComparison.Ordinal)
        && argument != GetOption(args, "--session") && argument != GetOption(args, "--agent")
        && argument != GetOption(args, "--task") && argument != GetOption(args, "--format"));
    if (target is null)
    {
        Console.Error.WriteLine("Usage: devx snapshot <file> --session <id>");
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
    var snapshotPayload = new Dictionary<string, string> { ["query"] = target };
    if (format == "agent") snapshotPayload["format"] = format;
    var request = new ProtocolRequest("1.0", ProtocolOperations.GetFileSnapshot, sessionId, JsonSerializer.SerializeToElement(snapshotPayload));
    var started = DateTimeOffset.UtcNow;
    var response = await engine.ExecuteAsync(request);
    await store.AddOperationMetricAsync(new OperationMetric("snapshot", started, DateTimeOffset.UtcNow - started, response.Success, sessionId, OperationMetadata(response, target), GetAgentLabel(args)));
    WriteResponse(response, format);
    return response.Success ? 0 : 1;
}

static async Task<int> MethodAsync(string connectionString, string[] args, string format)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: devx method <summary|get> <method> --session <id>");
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
        Console.Error.WriteLine("Usage: devx method <summary|get> <method> --session <id>");
        return 1;
    }

    var request = new ProtocolRequest("1.0", operation, sessionId, JsonSerializer.SerializeToElement(new { query = args[2] }));
    var started = DateTimeOffset.UtcNow;
    var response = await engine.ExecuteAsync(request);
    await store.AddOperationMetricAsync(new OperationMetric($"method {action}", started, DateTimeOffset.UtcNow - started, response.Success, sessionId, OperationMetadata(response, args[2]), GetAgentLabel(args)));
    WriteResponse(response, format);
    return response.Success ? 0 : 1;
}

static async Task<int> TypeAsync(string connectionString, string[] args, string format)
{
    if (args.Length < 3 || args[1] != "get")
    {
        Console.Error.WriteLine("Usage: devx type get <type> --session <id>");
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
    await store.AddOperationMetricAsync(new OperationMetric("type get", started, DateTimeOffset.UtcNow - started, response.Success, sessionId, OperationMetadata(response, args[2]), GetAgentLabel(args)));
    WriteResponse(response, format);
    return response.Success ? 0 : 1;
}

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
    await store.AddOperationMetricAsync(new OperationMetric("get references", started, DateTimeOffset.UtcNow - started, response.Success, sessionId, OperationMetadata(response, args[1]), GetAgentLabel(args)));
    WriteResponse(response, format);
    return response.Success ? 0 : 1;
}

static async Task<int> ReadAsync(string connectionString, string[] args, string format)
{
    var positional = args.Skip(1).TakeWhile(argument => !argument.StartsWith("--", StringComparison.Ordinal)).ToArray();
    var inferredKind = positional.Length == 1;
    if (positional.Length > 1 && positional[0] is not ("file" or "method" or "type" or "section"))
    {
        var failed = false;
        foreach (var target in positional)
        {
            Console.WriteLine($"=== {target} ===");
            var one = await ReadAsync(connectionString, ["read", target, .. args.Skip(1 + positional.Length)], format);
            if (one != 0) failed = true;
        }
        return failed ? 2 : 0;
    }
    if (positional.Length == 1)
    {
        var target = positional[0];
        var kind = target.Contains('#') ? "section" :
            ((target.Contains(Path.DirectorySeparatorChar) || Path.HasExtension(target)) && IOFile.Exists(target) ? "file" : "method");
        var remainder = args.Skip(2).ToArray();
        args = ["read", kind, target, .. remainder];
        if (kind == "file" && !HasFlag(args, "--full") && GetOption(args, "--range") is null) args = [.. args, "--full"];
        positional = [kind, target];
    }
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: devx read <name|file#Heading|file> [<name> ...] [--session <id>] | devx read <file|method|type|section> <target> [--range start:end|--full]");
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
    var readTarget = args[2];
    if (action is "method" or "type" && GetOption(args, "--id") is null && GetOption(args, "--query") is null && !readTarget.Contains('.'))
    {
        var lookup = await engine.ExecuteAsync(new ProtocolRequest("1.0", ProtocolOperations.SearchSymbol, sessionId, JsonSerializer.SerializeToElement(new { query = readTarget })));
        if (lookup.Data is { ValueKind: JsonValueKind.Object } result && TryGetPropertyIgnoreCase(result, "Symbols", out var symbols) && symbols.ValueKind == JsonValueKind.Array)
        {
            var exact = symbols.EnumerateArray().Where(symbol => TryGetPropertyIgnoreCase(symbol, "Name", out var name) && name.GetString() == readTarget).ToArray();
            if (exact.Length > 1)
            {
                Console.WriteLine($"ambiguous: {readTarget} matches {exact.Length} symbols");
                foreach (var candidate in exact)
                {
                    var qname = JsonPropertyText(candidate, "QualifiedName");
                    var path = JsonPropertyText(candidate, "FilePath");
                    var startLine = JsonPropertyText(candidate, "StartLine");
                    var endLine = JsonPropertyText(candidate, "EndLine");
                    var kind = JsonPropertyText(candidate, "Kind");
                    Console.WriteLine($"{qname}  {path}:{startLine}-{endLine}  {kind}");
                }
                return 2;
            }
            if (exact.Length == 1)
            {
                var symbolKind = JsonPropertyText(exact[0], "Kind");
                if (inferredKind && symbolKind is "Class" or "Type" or "Record" or "Interface" or "Struct" or "Enum") action = "type";
            }
        }
    }

    if (action == "file")
    {
        var full = HasFlag(args, "--full");
        var range = GetOption(args, "--range");
        if (!full && range is null)
        {
            Console.Error.WriteLine("Usage: devx read file <file> --range start:end --session <id> | --full");
            return 1;
        }

        request = new ProtocolRequest("1.0", "READ_FILE", sessionId, JsonSerializer.SerializeToElement(new { query = readTarget, range, full }), GetAgentLabel(args));
    }
    else if (action == "method")
    {
        var id = GetOption(args, "--id");
        var query = GetOption(args, "--query");
        if (id is null && query is null)
        {
            query = readTarget;
        }

        request = id is not null
            ? new ProtocolRequest("1.0", "READ_METHOD", sessionId, JsonSerializer.SerializeToElement(new { id }), GetAgentLabel(args))
            : new ProtocolRequest("1.0", "READ_METHOD", sessionId, JsonSerializer.SerializeToElement(new { query }), GetAgentLabel(args));
    }
    else if (action == "type")
    {
        var id = GetOption(args, "--id");
        var query = GetOption(args, "--query");
        if (id is null && query is null)
        {
            query = readTarget;
        }

        request = id is not null
            ? new ProtocolRequest("1.0", "READ_TYPE", sessionId, JsonSerializer.SerializeToElement(new { id }), GetAgentLabel(args))
            : new ProtocolRequest("1.0", "READ_TYPE", sessionId, JsonSerializer.SerializeToElement(new { query }), GetAgentLabel(args));
    }
    else if (action == "section")
    {
        var separator = args[2].LastIndexOf('#');
        if (separator <= 0)
        {
            // 19 of 23 `read section` calls in the v2 arm-B transcripts failed on exactly
            // this -- a missing '#Heading'. Answer with the headings rather than an error,
            // so the retry is a paste instead of another search. A trailing '#' is not this
            // case: it names nothing, and the engine answers it with the file itself.
            var snapshot = await engine.ExecuteAsync(new ProtocolRequest("1.0", ProtocolOperations.GetFileSnapshot, sessionId, JsonSerializer.SerializeToElement(new { query = args[2] }), GetAgentLabel(args)));
            Console.Error.WriteLine("devx read section needs a heading: devx read section <file>#<heading>");
            var headings = new List<string>();
            var payload = snapshot.Data;
            // The snapshot payload nests its collections under Data.Snapshot.
            if (payload is { ValueKind: JsonValueKind.Object } outer && TryGetPropertyIgnoreCase(outer, "Snapshot", out var inner))
                payload = inner;
            if (snapshot.Success && payload is { ValueKind: JsonValueKind.Object } snapData)
                foreach (var collection in new[] { "Types", "Methods" })
                    if (TryGetPropertyIgnoreCase(snapData, collection, out var items) && items.ValueKind == JsonValueKind.Array)
                        headings.AddRange(items.EnumerateArray().Select(item => JsonPropertyText(item, "Name")).Where(name => name.Length > 0));
            if (headings.Count == 0) Console.Error.WriteLine($"no headings found in '{args[2]}' (is it indexed?)");
            else
            {
                Console.Error.WriteLine($"headings in {args[2]}:");
                foreach (var heading in headings) Console.Error.WriteLine($"  devx read section \"{args[2]}#{heading}\"");
            }
            return 1;
        }

        request = new ProtocolRequest("1.0", "READ_SECTION", sessionId, JsonSerializer.SerializeToElement(new
        {
            file = args[2][..separator],
            heading = args[2][(separator + 1)..]
        }), GetAgentLabel(args));
    }
    else
    {
        Console.Error.WriteLine("Usage: devx read <name|file#Heading|file> [<name> ...] [--session <id>] | devx read <file|method|type|section> <target> [--range start:end|--full]");
        return 1;
    }

    var response = await engine.ExecuteAsync(request);
    await store.AddOperationMetricAsync(new OperationMetric($"read {action}", started, DateTimeOffset.UtcNow - started, response.Success, sessionId, OperationMetadata(response, args[2]), GetAgentLabel(args)));
    if (format is "json" or "agent" || action == "type")
    {
        WriteResponse(response, format);
    }
    else
    {
        WriteSourceResponse(response);
    }
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

static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
{
    if (element.ValueKind == JsonValueKind.Object)
    {
        foreach (var property in element.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
    }
    value = default;
    return false;
}

static string JsonPropertyText(JsonElement element, string name)
    => TryGetPropertyIgnoreCase(element, name, out var value) ? value.ToString() : string.Empty;

static async Task<int> CallersAsync(string connectionString, string[] args, string format)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: devx callers <method> --session <id>");
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
    await store.AddOperationMetricAsync(new OperationMetric("get callers", started, DateTimeOffset.UtcNow - started, response.Success, sessionId, OperationMetadata(response, args[1]), GetAgentLabel(args)));
    WriteResponse(response, format);
    return response.Success ? 0 : 1;
}

static async Task<int> CalleesAsync(string connectionString, string[] args, string format)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: devx callees <method> --session <id>");
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
    await store.AddOperationMetricAsync(new OperationMetric("get callees", started, DateTimeOffset.UtcNow - started, response.Success, sessionId, OperationMetadata(response, args[1]), GetAgentLabel(args)));
    WriteResponse(response, format);
    return response.Success ? 0 : 1;
}

static async Task<int> ReportAsync(string connectionString, string[] args, string format)
{
    if (args.Length > 1 && args[1] is "html" or "export")
        return await AnalyticsReportCommand.RunAsync(connectionString, args);

    var sessionId = GetOption(args, "--session");
    if (sessionId is null)
    {
        Console.Error.WriteLine("Missing --session. Report requires explicit session selection.");
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
    var report = await new DevXReportComposer().ComposeAsync(session, metrics, store);

    if (format == "json")
    {
        if (args.Contains("--anonymize", StringComparer.Ordinal)) report = ReportAnonymizer.Anonymize(report);
        var json = JsonSerializer.Serialize(report);
        var blockedPath = GetOption(args, "--blocked-terms");
        if (blockedPath is not null)
        {
            IReadOnlyList<string> matches;
            try { matches = await BlockedTerms.FindMatchesAsync(blockedPath, json); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Console.Error.WriteLine($"Could not read blocked-terms file: {ex.Message}"); return 2; }
            var count = matches.Sum(term => CountReportTerm(json, term));
            if (count > 0) { Console.Error.WriteLine($"blocked terms found: {count} ({matches.Count} terms)"); return 3; }
        }
        Console.WriteLine(json);
        return 0;
    }

    Console.WriteLine("Session Summary");
    Console.WriteLine($"Session: {session.Id.Value}");
    Console.WriteLine($"Repository: {session.RepositoryId.Value}");
    Console.WriteLine($"Searches: {session.SearchHistory.Count}");
    Console.WriteLine();
    Console.WriteLine("DevX Operation Log");
    foreach (var metric in report.OperationLog)
    {
        Console.WriteLine($"{metric.Operation} | {metric.Timestamp:O} | {metric.DurationMs}ms | {metric.Success}");
    }

    return 0;
}

static int CountReportTerm(string content, string term)
{
    var count = 0;
    var index = 0;
    while ((index = content.IndexOf(term, index, StringComparison.OrdinalIgnoreCase)) >= 0)
    { count++; index += term.Length; }
    return count;
}

static async Task<int> AskAsync(string connectionString, string[] args, string format)
{
    var useSandbox = args.Contains("--sandbox");
    var traceAsk = HasFlag(args, "--trace-ask") || HasFlag(args, "--debug-ask");
    var useContextWorkflow = HasFlag(args, "--context-workflow");

    // Refuse before anything else -- before reading stdin, before touching the index, and
    // well before any HTTP attempt. `devx ask` with no configured endpoint used to dial
    // http://localhost:8080 and fail slowly with a bare connection error.
    var llmConfig = LoadLlmConfig(FindRepositoryRoot(Environment.CurrentDirectory));
    if (llmConfig is null)
    {
        Console.Error.WriteLine(
            "devx ask is not configured (set llm.endpoint in .devx/config.json)");
        return 1;
    }

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

    if (useContextWorkflow)
    {
        var bootstrap = await AskBootstrap.InitializeAsync(connectionString);
        Console.WriteLine("phase=Bootstrap");
        Console.WriteLine($"index_created={bootstrap.IndexCreated.ToString().ToLowerInvariant()}");
        Console.WriteLine($"session_created={bootstrap.SessionCreated.ToString().ToLowerInvariant()}");
        Console.WriteLine($"session_reused={(!bootstrap.SessionCreated).ToString().ToLowerInvariant()}");
        Console.WriteLine("phase=RepoMap");

        using var contextLlm = LlmClientFactory.Create(llmConfig);
        var contextEngine = new ProtocolEngine(new SearchService(store, (ISymbolStore)store), (ISessionStore)store, store);
        var contextWorkflow = new AskContextWorkflow(contextLlm, contextEngine, traceAsk);
        var contextResult = await contextWorkflow.RunAsync(userPrompt, bootstrap.RepoMap, bootstrap.Session);
        Console.WriteLine();
        if (contextResult is null || !contextResult.Success || contextResult.SourceBlocks is null || contextResult.SourceBlocks.Count == 0)
        {
            var summary = contextResult?.Summary ?? "Context workflow failed.";
            if (!string.IsNullOrWhiteSpace(contextResult?.FailureReason))
            {
                summary = $"{summary} reason={contextResult.FailureReason}";
            }

            Console.WriteLine($"Completed: {summary}");
            return 1;
        }
        if (!string.IsNullOrWhiteSpace(contextResult.Summary))
        {
            Console.WriteLine($"Completed: {contextResult.Summary}");
        }
        if (!string.IsNullOrWhiteSpace(contextResult.EditProposal))
        {
            Console.WriteLine("Context workflow complete; model produced an edit proposal.");
            Console.WriteLine("Proposal:");
            Console.WriteLine(contextResult.EditProposal);
            return 0;
        }

        var editLlm = LlmClientFactory.Create(llmConfig);
        var editWorkflow = new AskEditWorkflow(editLlm, traceAsk);
        if (traceAsk)
        {
            Console.WriteLine("edit_workflow_start");
        }
        var editResult = await editWorkflow.RunAsync(userPrompt, contextResult, Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "src/DevX.Cli/Program.cs")));
        Console.WriteLine();
        Console.WriteLine($"Completed: {editResult.Summary}");
        return editResult.Success ? 0 : 1;
    }

    var sessionResolution = await ResolveSessionAsync(connectionString, args, allowCreate: true, allowReuse: true);
    var session = sessionResolution.Session;
    if (session is null)
    {
        Console.Error.WriteLine(sessionResolution.ErrorMessage ?? "Session not found.");
        return 1;
    }

    var root = await store.GetRepositoryRootPathAsync(session.RepositoryId);
    var repositoryRoot = root ?? Environment.CurrentDirectory;
    // The session's own repository root wins if it has a config; otherwise keep the config
    // the gate above already resolved. Never invent an endpoint (1.6.11).
    llmConfig = LoadLlmConfig(repositoryRoot) ?? llmConfig;
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
    var orchestrator = new AskOrchestrator(llm, engine, llmConfig, useSandbox ? sandbox?.SandboxRoot : null);
    var started = DateTimeOffset.UtcNow;

    Console.WriteLine("═══════════════════════════════════════════════════");
    Console.WriteLine("  DevX Ask — LLM-driven code orchestration");
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
                Console.WriteLine($"  [Turn {turnIndex}] 🔧 DevX → {status}");
            }
            else if (turn.ToolName == "build")
            {
                var success = turn.Content.Contains("\"Success\":true") || turn.Content.Contains("\"success\":true");
                var status = success ? "✅ BUILD PASSED" : "❌ BUILD FAILED";
                Console.WriteLine($"  [Turn {turnIndex}] 🔧 DevX → {status}");
            }
            else if (turn.ToolName == "test")
            {
                var success = turn.Content.Contains("\"Success\":true") || turn.Content.Contains("\"success\":true");
                var status = success ? "✅ TESTS PASSED" : "❌ TESTS FAILED";
                Console.WriteLine($"  [Turn {turnIndex}] 🔧 DevX → {status}");
            }
            else
            {
                Console.WriteLine($"  [Turn {turnIndex}] 🔧 DevX → Result for {turn.ToolName} received");
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
    await store.AddOperationMetricAsync(new OperationMetric("ask", started, DateTimeOffset.UtcNow - started, askResult.Success, session.Id.Value.ToString(), AgentLabel: GetAgentLabel(args)));

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
    var store = new SqliteStorageInitializer(connectionString);
    await store.InitializeAsync();
    var (repositoryRoot, indexedFiles, repositoryIndexed, _) = await ResolveRuntimeReadinessInputsAsync(store, args);
    var health = await new RuntimeHealthService().EvaluateAsync(repositoryRoot, repositoryIndexed, indexedFiles);
    var response = CapabilitiesComposer.Compose(health);

    if (format == "json")
    {
        Console.WriteLine(JsonSerializer.Serialize(response));
        return 0;
    }

    Console.WriteLine("DevX Capabilities");
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

// `devx ask` needs an LLM endpoint the user actually configured. Nothing else in the CLI
// does, so this is what decides whether `ask` is advertised -- and whether it is allowed to
// try at all. Before 1.6.11 an unconfigured repo silently fell back to
// http://localhost:8080 and the call failed minutes later with a connection error that told
// the agent nothing; the endpoint is never guessed now.
static bool IsAskConfigured(string path) => LoadLlmConfig(FindRepositoryRoot(path)) is not null;

static LlmConfig? LoadLlmConfig(string repositoryRoot)
{
    var configPath = Path.Combine(repositoryRoot, ".devx", "config.json");
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
    const string safeError = "The LLM request could not be completed. See local server diagnostics for details.";
    if (format == "json")
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Success = false,
            Error = safeError,
            Diagnostics = diagnostics with { FinalFailureReason = safeError },
            AssistantMessage = assistantMessage,
            Usage = usage
        }));
        return;
    }

    Console.Error.WriteLine(safeError);
    Console.Error.WriteLine($"Expected endpoint: {LlmErrorRedaction.SafeEndpoint(endpoint)}");
}

static async Task<int> RunToolAsync(string connectionString, string[] args, string format)
{
    if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
    {
        Console.Error.WriteLine("Usage: devx run <repository-tool> --session <id> [-- <args...>]");
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
    var session = await store.GetSessionAsync(new GetSessionRequest(new SessionId(Guid.Parse(sessionId))));
    if (session is null)
    {
        Console.Error.WriteLine("Session not found.");
        return 1;
    }

    var repositoryRoot = Path.GetFullPath(await store.GetRepositoryRootPathAsync(session.RepositoryId)
        ?? Environment.CurrentDirectory);
    var toolPath = Path.GetFullPath(Path.Combine(repositoryRoot, args[1]));
    var rootPrefix = repositoryRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!toolPath.StartsWith(rootPrefix, StringComparison.Ordinal) || !IOFile.Exists(toolPath))
    {
        Console.Error.WriteLine("Tool must be an existing file inside the indexed repository.");
        return 1;
    }

    string executable;
    var toolArguments = new List<string>();
    if (Path.GetExtension(toolPath).Equals(".py", StringComparison.OrdinalIgnoreCase))
    {
        var venvPython = Path.Combine(repositoryRoot, ".venv", "bin", "python");
        executable = IOFile.Exists(venvPython) ? venvPython : "python3";
        toolArguments.Add(toolPath);
    }
    else if (Path.GetExtension(toolPath).Equals(".sh", StringComparison.OrdinalIgnoreCase))
    {
        executable = "/bin/zsh";
        toolArguments.Add(toolPath);
    }
    else
    {
        executable = toolPath;
    }

    var separator = Array.IndexOf(args, "--");
    if (separator >= 0)
    {
        toolArguments.AddRange(args.Skip(separator + 1));
    }

    var startInfo = new System.Diagnostics.ProcessStartInfo(executable)
    {
        WorkingDirectory = repositoryRoot,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };
    foreach (var argument in toolArguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    var started = DateTimeOffset.UtcNow;
    using var process = System.Diagnostics.Process.Start(startInfo);
    if (process is null)
    {
        Console.Error.WriteLine("Failed to start repository tool.");
        return 1;
    }

    var stdoutTask = process.StandardOutput.ReadToEndAsync();
    var stderrTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var stdout = await stdoutTask;
    var stderr = await stderrTask;
    var success = process.ExitCode == 0;
    await store.AddOperationMetricAsync(new OperationMetric(
        "run", started, DateTimeOffset.UtcNow - started, success, sessionId,
        success ? null : JsonSerializer.Serialize(new { ErrorCode = "ToolFailed", FailureReason = stderr, AttemptedName = args[1] }),
        GetAgentLabel(args)));

    if (format == "json")
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Success = success,
            Tool = Path.GetRelativePath(repositoryRoot, toolPath),
            ExitCode = process.ExitCode,
            Stdout = stdout,
            Stderr = stderr
        }));
    }
    else
    {
        if (!string.IsNullOrEmpty(stdout)) Console.Write(stdout);
        if (!string.IsNullOrEmpty(stderr)) Console.Error.Write(stderr);
    }

    return process.ExitCode;
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

static IReadOnlyList<string> GetPositionals(string[] args, int startIndex)
{
    var values = new List<string>();
    for (var index = startIndex; index < args.Length; index++)
    {
        if (args[index].StartsWith("--", StringComparison.Ordinal))
        {
            if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)) index++;
            continue;
        }
        values.Add(args[index]);
    }
    return values;
}

static string? GetAgentLabel(string[] args)
    => GetOption(args, "--agent")
       ?? Environment.GetEnvironmentVariable("DEVX_AGENT")
       ?? Environment.GetEnvironmentVariable("CODEX_AGENT_NAME");

static string? OperationMetadata(ProtocolResponse response, string? attemptedName = null)
{
    var responseCharacters = response.Data?.GetRawText().Length ?? response.Errors.Sum(error => error.Code.Length + error.Message.Length);
    var estimatedTokens = (int)Math.Ceiling(responseCharacters / 4d);
    string? summary = null;
    if (response.Data is { ValueKind: JsonValueKind.Object } data
        && data.TryGetProperty("Execution", out var execution)
        && execution.ValueKind == JsonValueKind.Object
        && execution.TryGetProperty("Summary", out var summaryElement)
        && summaryElement.ValueKind == JsonValueKind.String)
    {
        summary = summaryElement.GetString();
    }

    if (response.Success)
    {
        string? resolvedVia = null;
        long? wholeFileCharacters = null;
        long? returnedCharacters = null;
        string? readEventId = null;
        if (response.Data is { ValueKind: JsonValueKind.Object } source && source.TryGetProperty("Metadata", out var metadata)
            && metadata.ValueKind == JsonValueKind.Object)
        {
            if (metadata.TryGetProperty("ResolvedVia", out var via)) resolvedVia = via.GetString();
            if (metadata.TryGetProperty("WholeFileCharacters", out var whole)) wholeFileCharacters = whole.GetInt64();
            if (metadata.TryGetProperty("ReturnedCharacters", out var returned)) returnedCharacters = returned.GetInt64();
            if (metadata.TryGetProperty("ReadEventId", out var readId)) readEventId = readId.GetString();
        }
        return JsonSerializer.Serialize(new
        {
            AttemptedName = attemptedName,
            Summary = summary,
            ResolvedVia = resolvedVia,
            WholeFileCharacters = wholeFileCharacters,
            ReturnedCharacters = returnedCharacters,
            ReadEventId = readEventId,
            ResponseCharacters = responseCharacters,
            EstimatedTokens = estimatedTokens
        });
    }

    var error = response.Errors.FirstOrDefault();
    return JsonSerializer.Serialize(new
    {
        ErrorCode = error?.Code ?? "UnknownFailure",
        FailureReason = error?.Message ?? "The operation failed without a protocol error.",
        AttemptedName = error?.AttemptedName ?? attemptedName,
        Suggestions = error?.Suggestions ?? [],
        Candidates = error?.Candidates,
        Summary = summary,
        ResponseCharacters = responseCharacters,
        EstimatedTokens = estimatedTokens
    });
}

static string ResolveDatabasePath()
{
    var stateDirectory = Path.Combine(Environment.CurrentDirectory, ".devx");
    var databasePath = Path.Combine(stateDirectory, "devx.db");
    Directory.CreateDirectory(stateDirectory);

    return databasePath;
}

static async Task<(string RepositoryRoot, int IndexedFiles, bool RepositoryIndexed, string? SessionId)> ResolveRuntimeReadinessInputsAsync(
    SqliteStorageInitializer store,
    string[] args)
{
    var sessionId = GetOption(args, "--session");
    if (!string.IsNullOrWhiteSpace(sessionId))
    {
        var session = await store.GetSessionAsync(new GetSessionRequest(new SessionId(Guid.Parse(sessionId))));
        if (session is not null)
        {
            var repositoryRootFromSession = await store.GetRepositoryRootPathAsync(session.RepositoryId);
            if (!string.IsNullOrWhiteSpace(repositoryRootFromSession))
            {
                return (
                    Path.GetFullPath(repositoryRootFromSession),
                    await store.CountIndexedFilesAsync(session.RepositoryId),
                    true,
                    sessionId);
            }
        }
    }

    var repositoryRoot = Path.GetFullPath(Environment.CurrentDirectory);
    var repository = await store.GetRepositoryByRootPathAsync(repositoryRoot);
    return (
        repositoryRoot,
        repository is null ? 0 : await store.CountIndexedFilesAsync(repository.Id),
        repository is not null,
        sessionId);
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
    return Path.Combine(".devx", "current-session", repositoryId.Value.ToString(), "active");
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

static string ResolveFormat(string[] args)
    => OutputFormat.Resolve(GetOption(args, "--format"), OutputFormat.LooksLikeAnAgent());

static void WriteResponse(ProtocolResponse response, string format,
    IReadOnlyDictionary<(string FileId, int Line), string>? enclosingSymbols = null)
{
    if (format == "json")
    {
        Console.WriteLine(JsonSerializer.Serialize(response));
        return;
    }

    if (format == "agent")
    {
        Console.WriteLine(FormatAgentResponse(response, enclosingSymbols));
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

static string FormatAgentResponse(ProtocolResponse response,
    IReadOnlyDictionary<(string FileId, int Line), string>? enclosingSymbols = null)
{
    if (!response.Success) return string.Join("\n", response.Errors.Select(error => $"error: {error.Message}"));
    if (response.Data is not { } data) return string.Empty;
    if (TryGetPropertyIgnoreCase(data, "symbols", out var symbols) && symbols.ValueKind == JsonValueKind.Array && symbols.GetArrayLength() > 0)
    {
        var hits = symbols.EnumerateArray().Take(10).Select(item => $"{JsonPropertyText(item, "Kind").ToLowerInvariant()} {JsonPropertyText(item, "QualifiedName")} {RelativeAgentPath(JsonPropertyText(item, "FilePath"))}:{JsonPropertyText(item, "StartLine")}-{JsonPropertyText(item, "EndLine")}").ToList();
        if (symbols.GetArrayLength() > hits.Count) hits.Add($"... {symbols.GetArrayLength() - hits.Count} more");
        return string.Join("\n", hits);
    }
    if (TryGetPropertyIgnoreCase(data, "contentMatches", out var matches) && matches.ValueKind == JsonValueKind.Array)
    {
        var hits = matches.EnumerateArray().Take(10).Select(item =>
        {
            var preview = JsonPropertyText(item, "Preview").Trim();
            var line = JsonPropertyText(item, "Line");
            var symbol = enclosingSymbols is not null
                && enclosingSymbols.TryGetValue((JsonPropertyText(item, "FileId"), int.TryParse(line, out var at) ? at : 0), out var name)
                    ? $" [{name}]"
                    : string.Empty;
            return $"{RelativeAgentPath(JsonPropertyText(item, "Path"))}:{line}: {preview[..Math.Min(120, preview.Length)]}{symbol}";
        }).ToList();
        if (matches.GetArrayLength() > hits.Count)
        {
            // The cap stays; the message now says what to do about it. "N more" alone told the
            // agent nothing, which is why the next call was usually the same search reworded.
            hits.Add($"... {matches.GetArrayLength() - hits.Count} more: narrow with a second word, "
                     + "alternation (a|b), or several queries in one call, e.g. devx search text \"a\" \"b\"");
        }

        return string.Join("\n", hits);
    }
    foreach (var collection in new[] { "Callers", "Callees", "References" })
    {
        if (!TryGetPropertyIgnoreCase(data, collection, out var items)) continue;
        if (items.ValueKind == JsonValueKind.Object && TryGetPropertyIgnoreCase(items, collection, out var nested)) items = nested;
        if (items.ValueKind == JsonValueKind.Array)
            return string.Join("\n", items.EnumerateArray().Select(item => $"{RelativeAgentPath(JsonPropertyText(item, "FilePath"))}:{JsonPropertyText(item, collection == "References" ? "LineNumber" : "CallSiteLine")}: {JsonPropertyText(item, "QualifiedName")}"));
    }
    if (TryGetPropertyIgnoreCase(data, "snapshot", out var snapshot))
    {
        var lines = new List<string> { $"{RelativeAgentPath(JsonPropertyText(snapshot, "Path"))} ({JsonPropertyText(snapshot, "Language")}, {JsonPropertyText(snapshot, "ApproximateLoc")} lines)" };
        if (TryGetPropertyIgnoreCase(snapshot, "types", out var types) && types.ValueKind == JsonValueKind.Array)
            lines.AddRange(types.EnumerateArray().Where(type => JsonPropertyText(type, "StartLine").Length > 0 && JsonPropertyText(type, "EndLine").Length > 0).Select(type => $"type {JsonPropertyText(type, "Name")} :{JsonPropertyText(type, "StartLine")}-{JsonPropertyText(type, "EndLine")}"));
        if (TryGetPropertyIgnoreCase(snapshot, "methods", out var methods) && methods.ValueKind == JsonValueKind.Array)
            lines.AddRange(methods.EnumerateArray().Where(method => JsonPropertyText(method, "StartLine").Length > 0 && JsonPropertyText(method, "EndLine").Length > 0).Select(method => $"  method {JsonPropertyText(method, "Name")}({AgentSignature(JsonPropertyText(method, "Signature"))}) :{JsonPropertyText(method, "StartLine")}-{JsonPropertyText(method, "EndLine")}"));
        return string.Join("\n", lines);
    }
    if (TryGetPropertyIgnoreCase(data, "path", out var path) && TryGetPropertyIgnoreCase(data, "source", out var source))
    {
        var p = RelativeAgentPath(path.GetString() ?? "");
        int.TryParse(JsonPropertyText(data, "StartLine"), out var start); int.TryParse(JsonPropertyText(data, "EndLine"), out var end);
        var name = JsonPropertyText(data, "QualifiedName");
        var kind = JsonPropertyText(data, "Kind");
        // A `read section <file>#<name>` asked for a heading and got a symbol; say which one,
        // so the caller can tell a resolution from a literal match.
        var resolved = string.Empty;
        if (TryGetPropertyIgnoreCase(data, "Metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object
            && JsonPropertyText(metadata, "ResolvedVia") == "section-symbol")
        {
            var note = JsonPropertyText(metadata, "Message");
            if (note.Length > 0) resolved = note + "\n";
        }
        return $"{resolved}{p}:{start}-{end}{(string.IsNullOrEmpty(name) ? "" : $" {name}")}{(string.IsNullOrEmpty(kind) ? "" : $" [{kind}]")}\n{source.GetString()}";
    }
    return data.GetRawText();
}

// The source an auto-read appends to a single-hit search result. An EXPLICIT read always
// returns its source (the 1.6.1 paging stands); this cap is only for source the agent did
// not ask for, so an unlucky one-hit search cannot dump a 900-line method into the context.
static string AutoReadText(ProtocolResponse read)
{
    const int AutoReadMaxLines = 80;
    var text = FormatAgentResponse(read);
    if (read.Data is not { ValueKind: JsonValueKind.Object } data) return text;
    var head = text.Split('\n')[0];
    int.TryParse(JsonPropertyText(data, "StartLine"), out var start);
    int.TryParse(JsonPropertyText(data, "EndLine"), out var end);
    var span = end >= start && start > 0 ? end - start + 1 : 0;
    if (span <= AutoReadMaxLines) return text;
    var body = text.Contains('\n') ? text[(text.IndexOf('\n') + 1)..] : string.Empty;
    var first = body.Split('\n').FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim() ?? string.Empty;
    return $"{head} {first}\nread to get {span} lines";
}

static string RelativeAgentPath(string path) => string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) ? path : Path.GetRelativePath(Environment.CurrentDirectory, path);
static string AgentSignature(string signature)
{
    var start = signature.IndexOf('(');
    var end = signature.IndexOf(')', start + 1);
    return start >= 0 && end > start ? signature[(start + 1)..end] : signature;
}

static void WriteSourceResponse(ProtocolResponse response)
{
    if (!response.Success || response.Data is null)
    {
        foreach (var error in response.Errors) Console.Error.WriteLine($"{error.Code}: {error.Message}");
        return;
    }

    var source = response.Data.Value.Deserialize<SourceReadResponse>();
    if (source is null)
    {
        Console.WriteLine(response.Data.Value.GetRawText());
        return;
    }

    // A section request that named a symbol rather than a heading resolves to it; print what
    // it resolved to, so the reader is not left guessing why the span is not the whole file.
    if (source.Metadata is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Object } metadata
        && metadata.TryGetProperty("ResolvedVia", out var resolvedVia) && resolvedVia.GetString() == "section-symbol"
        && metadata.TryGetProperty("Message", out var resolvedMessage) && resolvedMessage.ValueKind == System.Text.Json.JsonValueKind.String)
    {
        Console.WriteLine(resolvedMessage.GetString());
    }
    var location = source.StartLine == source.EndLine
        ? $"{source.Path}:{source.StartLine}"
        : $"{source.Path}:{source.StartLine}-{source.EndLine}";
    Console.WriteLine(location);
    if (source.SourceReturned && source.Source.Length > 0) Console.WriteLine(source.Source);
}

static void PrintVersion(string format)
{
    var version = GetProductVersion();

    if (format == "json")
    {
        Console.WriteLine(JsonSerializer.Serialize(new { version }));
        return;
    }

    Console.WriteLine(version);
}

static string GetProductVersion()
    => typeof(ArtifactCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(ArtifactCommand).Assembly.GetName().Version?.ToString()
        ?? "unknown";

static void PrintHelp()
{
    Console.WriteLine("""
1. devx search <name>   (symbols; falls back to text)
2. devx read <name> [more names]   (source of a symbol, file, or file#Heading)
3. devx replace <file> --old <text> --new <text>   (edit; add --all or --occurrence n)
4. devx test [file|dir]   (build+test summary)
Add --format agent for compact output; no session needed.

Usage:
  devx version [--format json]
  devx --version
  devx init [--agents claude,codex] [--global] [--format json]
  devx index <path>
  devx index status [--session <id>]
  devx index update <file> [<file> ...] --session <id>
  devx index changed --session <id>
  devx search <query> [--session <id>] [--agent <label>]
  devx search <symbol|text> <query> [--session <id>] [--agent <label>]
  devx snapshot <file> --session <id> [--agent <label>]
  devx read <name|file#Heading|file> [<name> ...] [--session <id>]
  devx read <file|method|type|section> <target> [--range start:end|--full] [--session <id>]
  devx artifact create <name> --content <text>|--content-file <path>|--content-json <json-string>|--content-stdin
  devx artifact delete <name>
  devx artifact path <name>
  devx patch create <output-file> --session <id> --op <Operation> --path <relative-path> [options] [--append]
  devx replace <file> --old <text> --new <text> [--all | --occurrence <n>]
  Editing: simple edit -> devx replace; structured/multi-file edits -> devx patch
  devx patch preview <patch-file> --session <id>
  devx patch validate <patch-file> --session <id>
  devx patch apply <patch-file> --session <id>
  devx patch rollback <patch-file> --session <id>
  devx health [--session <id>]
  devx doctor [--session <id>]
  devx build --session <id> [--runtime <kind>] [--target <path|name>]
  devx test [<file> ...] --session <id> [--runtime <kind>] [--target <path|name>] [--timeout <seconds>]
  devx report html [--period 30d] [--session <id>] [--output <file>] [--format html|json] [--anonymize] [--blocked-terms <file>]
  devx report --session <id> --format json [--anonymize] [--blocked-terms <file>]
  devx report html --input <events.json> [--org <name>] [--team <name>] [--repo <name>]
  devx report html --input <a.json> --input <b.json> [--period 30d] [--output <file>]
  devx report export [--period 30d] [--session <id>] --output <events.json>
  devx fallback record --session <id> --capability <id> --failed-command <command> --reason <reason> --action <fallback-action>
  devx fallback observe --session <id> --agent <label> --command <shell-command> [--file <path> ...]
  devx fallback observe --session <id> --agent <label> --coverage-start <ISO8601> --coverage-end <ISO8601>
  devx capabilities [--format json]
  devx mcp serve

Patch create options:
  --op <Operation>
  --path <relative-path>
  --append
  --content <text>
  --content-file <path>
  --content-stdin
  --replacement <text>
  --replacement-file <path>
  --replacement-stdin
  --method <name>
  --method-text <text>
  --method-text-file <path>
  --position <value>
  --namespace <name>
  --containing-type <name>
  --type-id <id>

Artifact options:
  --content <text>
  --content-file <path>
  --content-json <json-string>
  --content-stdin

Global options:
  --format <json|text|agent>
  --session <id>
  --agent <label>
  --task <label>   Attribute recorded calls to a brief/task (caller supplied)

Notes:
  build, test, and report require explicit --session selection.
""");
    // `ask` only exists for a repo that configured an endpoint, so it is advertised only
    // there -- an unconfigured repo gets no pointer to a command that will refuse to run.
    if (IsAskConfigured(Environment.CurrentDirectory))
    {
        Console.WriteLine("  devx ask \"<question>\"   ask the LLM configured in .devx/config.json");
    }
}
