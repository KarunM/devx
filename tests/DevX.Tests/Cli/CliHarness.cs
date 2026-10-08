using DevX.Cli;
using Xunit;

namespace DevX.Tests.Cli;

[CollectionDefinition("ProcessGlobalState", DisableParallelization = true)]
public sealed class CliProcessGlobalState { }

/// <summary>
/// Drives the real CLI entry point in a throwaway directory. The OSS11 bugs lived in
/// argument dispatch, so the tests have to go through the front door rather than call a
/// helper directly.
/// </summary>
[Collection("ProcessGlobalState")]
public abstract class CliHarness
{
    protected const string Guid0 = "11111111-1111-1111-1111-111111111111";

    protected static async Task<(int ExitCode, string Output)> RunAsync(
        string root, params string[] args)
    {
        var originalDirectory = Environment.CurrentDirectory;
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        try
        {
            Environment.CurrentDirectory = root;
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var entryPoint = typeof(ArtifactCommand).Assembly.EntryPoint!;
            var invocation = entryPoint.Invoke(null, [args]);
            var exitCode = invocation is Task<int> task ? await task : Convert.ToInt32(invocation);
            return (exitCode, stdout.ToString() + stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            Environment.CurrentDirectory = originalDirectory;
        }
    }

    protected static string NewRoot(string prefix = "devx-oss11-") =>
        Directory.CreateTempSubdirectory(prefix).FullName;

    protected static async Task WithRoot(string root, Func<string, Task> body)
    {
        try
        {
            await body(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
