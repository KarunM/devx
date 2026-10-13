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

    if (!args.Contains("--session", StringComparer.Ordinal) && args[0] is ("index" or "search" or "read" or "method" or "type" or "callers" or "callees" or "references" or "snapshot" or "test" or "build" or "verify" or "run" or "replace" or "patch"))
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
                // Re-indexes the edited file by default; `--no-index` opts out.
                args.Contains("--no-index", StringComparer.Ordinal)
                    ? null
                    : async file => { await replaceIndexer.ReindexFilesAsync(Environment.CurrentDirectory, [file]); },
                replaceStore);
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

        case "verify":
            return await RunVerifyAsync(connectionString, args, format);

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
