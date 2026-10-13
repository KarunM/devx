using System.Text.Json;
using DevX.Application;
using DevX.Domain;
using DevX.Cli;
using DevX.Infrastructure.Storage.SQLite;
using DevX.Indexer;
using DevX.Protocol;
using Microsoft.Data.Sqlite;
using IOFile = System.IO.File;

static partial class Program
{
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
        // `--verify[=build|test|both]` on the edit commands. Opt-in, so a patch applied without the
        // flag does not build or run tests.
        var verifyMode = VerifyAfterEdit.ParseVerifyFlag(args);
        var patchedPaths = verifyMode is null ? [] : PatchPaths(document.RootElement);
        var request = new ProtocolRequest("1.0", ProtocolOperations.Patch, sessionId, document.RootElement.Clone());
        var started = DateTimeOffset.UtcNow;
        var response = await engine.ExecuteAsync(request);
        await store.AddOperationMetricAsync(new OperationMetric("patch", started, DateTimeOffset.UtcNow - started, response.Success, sessionId, OperationMetadata(response, patchPath), GetAgentLabel(args)));
        WriteResponse(response, format);
        if (!response.Success) return 1;
        return verifyMode is { } mode && patchedPaths.Count > 0
            ? await VerifyPatchedFilesAsync(format, mode, patchedPaths)
            : 0;
    }

    // Every `path` the patch names, at any depth: one file for a ReplaceMethod, several for a
    // multi-file patch. Blank and duplicate values are dropped.
    static IReadOnlyList<string> PatchPaths(JsonElement patch)
    {
        var paths = new List<string>();
        void Walk(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("path") && property.Value.ValueKind == JsonValueKind.String)
                    {
                        var value = property.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(value) && !paths.Contains(value, StringComparer.Ordinal)) paths.Add(value);
                    }
                    else Walk(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray()) Walk(item);
            }
        }
        Walk(patch);
        return paths;
    }

    // The verify half of `devx patch apply --verify`: the build for the first patched file (which
    // locates its project) and the relevant tests of every patched file, in one response.
    static async Task<int> VerifyPatchedFilesAsync(string format, VerifyMode mode, IReadOnlyList<string> paths)
    {
        var root = FindRepositoryRoot(Environment.CurrentDirectory);
        var runner = new DotnetBuildTestService();
        var build = mode == VerifyMode.Test
            ? VerifyAfterEdit.TestOnlyBuild(paths[0])
            : await VerifyAfterEdit.RunBuildAsync(runner, root, paths[0]);
        ExecutionRecord? tests = null;
        if (build.Success && mode != VerifyMode.Build)
        {
            var relevant = paths.SelectMany(path => VerifyAfterEdit.FindRelevantTestFiles(root, path))
                .Distinct(StringComparer.Ordinal).ToArray();
            tests = await runner.RunTestsAsync(root, relevant, TimeSpan.FromSeconds(300));
        }
        var verification = new VerifyAfterEditResult(build, tests, build.Summary.StartsWith("skip", StringComparison.OrdinalIgnoreCase));
        var success = build.Success && (tests?.Success ?? true);
        if (format == "json")
        {
            Console.WriteLine(JsonSerializer.Serialize(new { Verify = new { Mode = VerifyAfterEdit.ModeName(mode), Build = build.Summary, Tests = tests?.Summary } }));
        }
        else if (success)
        {
            Console.WriteLine(VerifyAfterEdit.FormatOneLine("patch", verification));
        }
        else
        {
            foreach (var line in VerifyAfterEdit.FormatVerifyLines(verification)) Console.WriteLine(line);
        }
        return success ? 0 : 1;
    }

    static async Task<int> ArtifactAsync(string[] args, string format)
        => await DevX.Cli.ArtifactCommand.RunAsync(args.Skip(1).ToArray(), format);

    // A write re-indexes the files it touched, by default, on every path -- replace, patch apply,
    // patch rollback, patch create/delete, and the MCP edits that go through the same
    // ProtocolEngine. `--no-index` is the opt-out.
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
}
