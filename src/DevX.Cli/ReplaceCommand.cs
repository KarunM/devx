using DevX.Protocol;
using DevX.Domain;
using DevX.Indexer;
using System.Text.Json;

namespace DevX.Cli;

public static class ReplaceCommand
{
    public static int Run(string[] args, string root)
        => RunAsync(args, root).GetAwaiter().GetResult();

    public static async Task<int> RunAsync(string[] args, string root, Func<string, VerifyAfterEdit>? verifierFactory = null, Func<string, Task>? reindexFile = null, IIndexFreshnessStore? freshnessStore = null)
    {
        string? Option(string key) { var i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        if (args.Length < 2 || Option("--old") is null || Option("--new") is null)
        { Console.Error.WriteLine("Usage: devx replace <file> --old <text> --new <text> [--all | --occurrence <n>] [--verify[=build|test|both] | --no-verify] [--no-index] [--format json]"); return 1; }
        int? occurrence = int.TryParse(Option("--occurrence"), out var n) ? n : null;
        // Opt-in, with a mode: a bare --verify is build+tests, --verify=build is the build alone.
        var verifyMode = VerifyAfterEdit.ParseVerifyFlag(args);
        if (freshnessStore is null)
        {
            Console.Error.WriteLine("replace failed: durable index freshness tracking is unavailable; no source was changed");
            return 1;
        }
        var repositoryRoot = Path.GetFullPath(root);
        var relativePath = args[1].Replace('\\', '/');
        if (!await freshnessStore.TryMarkIndexFilesDirtyAsync(repositoryRoot, [relativePath]))
        {
            Console.Error.WriteLine("replace failed: this repository already has unreconciled source changes; run 'devx index <root>' first");
            return 1;
        }
        var result = new TextReplaceService().Apply(root, args[1], Option("--old")!, Option("--new")!, args.Contains("--all"), occurrence);
        if (!result.Success)
        {
            await freshnessStore.ClearDirtyIndexFilesAsync(repositoryRoot, [relativePath]);
            Console.WriteLine(result.Message);
            return 1;
        }
        if (args.Contains("--no-index", StringComparer.Ordinal))
        {
            Console.WriteLine($"{result.Message}; index remains fenced until 'devx index {repositoryRoot}' succeeds");
            return 0;
        }
        if (reindexFile is null)
        {
            Console.Error.WriteLine("replace applied, but index refresh is unavailable; indexed operations are blocked until reconciliation");
            return 1;
        }
        try { await reindexFile(args[1]); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException or Microsoft.Data.Sqlite.SqliteException)
        {
            // A callback can fail after partially completing refresh work. Reassert this
            // path's fence before reporting an unsuccessful refresh.
            await freshnessStore.MarkIndexFilesDirtyAsync(repositoryRoot, [relativePath]);
            Console.Error.WriteLine($"replace applied, but index refresh failed; indexed operations are blocked until reconciliation: {error.Message}");
            return 1;
        }
        if (await freshnessStore.GetDirtyIndexRepositoryAsync(repositoryRoot) is not null)
        {
            Console.Error.WriteLine("replace applied, but index refresh was incomplete; indexed operations are blocked until reconciliation");
            return 1;
        }
        if (verifyMode is null)
        {
            Console.WriteLine(result.Message);
            if (result.Diff.Length > 0) Console.WriteLine(result.Diff);
            return 0;
        }

        var service = new DotnetBuildTestService();
        var verifier = verifierFactory?.Invoke(root) ?? new VerifyAfterEdit(
            file => VerifyAfterEdit.RunBuildAsync(service, root, file),
            files => service.RunTestsAsync(root, files, TimeSpan.FromSeconds(300)),
            file => VerifyAfterEdit.FindRelevantTestFiles(root, file));
        var verification = await verifier.RunAsync(args[1], verifyMode.Value);
        var success = verification.Build.Success && (verification.Tests?.Success ?? true);
        if (Option("--format") == "json")
        {
            Console.WriteLine(JsonSerializer.Serialize(new { result.Message, result.Diff, Verify = new { Build = Summary(verification.Build), Tests = verification.Tests is null ? null : Summary(verification.Tests) } }));
        }
        else if (success)
        {
            // The edit, then ONE line for the verification (brief OSS14 adjustment, 2026-10-09).
            Console.WriteLine(result.Message);
            if (result.Diff.Length > 0) Console.WriteLine(result.Diff);
            Console.WriteLine(VerifyAfterEdit.FormatOneLine("edit", verification));
        }
        else
        {
            foreach (var line in VerifyAfterEdit.FormatLines(result.Message, result.Diff, verification)) Console.WriteLine(line);
        }
        return success ? 0 : 1;
    }

    private static object Summary(ExecutionRecord record) => new { record.Success, record.Summary, record.Diagnostics };
}
