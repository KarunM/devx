using DevX.Protocol;
using DevX.Domain;
using DevX.Indexer;
using System.Text.Json;

namespace DevX.Cli;

public static class ReplaceCommand
{
    public static int Run(string[] args, string root)
        => RunAsync(args, root).GetAwaiter().GetResult();

    public static async Task<int> RunAsync(string[] args, string root, Func<string, VerifyAfterEdit>? verifierFactory = null, Func<string, Task>? reindexFile = null)
    {
        string? Option(string key) { var i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        if (args.Length < 2 || Option("--old") is null || Option("--new") is null)
        { Console.Error.WriteLine("Usage: devx replace <file> --old <text> --new <text> [--all | --occurrence <n>] [--verify[=build|test|both] | --no-verify] [--no-index] [--format json]"); return 1; }
        int? occurrence = int.TryParse(Option("--occurrence"), out var n) ? n : null;
        // Opt-in, with a mode: a bare --verify is build+tests, --verify=build is the build alone.
        var verifyMode = VerifyAfterEdit.ParseVerifyFlag(args);
        var result = new TextReplaceService().Apply(root, args[1], Option("--old")!, Option("--new")!, args.Contains("--all"), occurrence);
        if (!result.Success) { Console.WriteLine(result.Message); return 1; }
        if (reindexFile is not null) await reindexFile(args[1]);
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
