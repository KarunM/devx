using DevX.Cli;
using System.Text.Json;
using Xunit;

namespace DevX.Tests.Cli;

[CollectionDefinition("ProcessGlobalState", DisableParallelization = true)]
public sealed class ProcessGlobalStateCollection { }

[Collection("ProcessGlobalState")]
public sealed class CliCommandTests
{
    private static readonly SemaphoreSlim ProcessStateLock = new(1, 1);
    [Theory]
    [InlineData("--version", 0, "1.6.2")]
    [InlineData("--help", 0, "Usage")]
    [InlineData("unknown-command", 1, "Unknown command")]
    [InlineData("index", 1, "Missing repository path")]
    [InlineData("health", 1, "Status: Unavailable")]
    [InlineData("doctor", 1, "Status: Unavailable")]
    [InlineData("capabilities", 0, "")]
    [InlineData("search", 1, "Usage: devx search")]
    [InlineData("session", 1, "")]
    [InlineData("patch", 1, "")]
    [InlineData("artifact", 1, "")]
    [InlineData("build", 1, "")]
    [InlineData("test", 1, "")]
    [InlineData("diagnostics", 1, "")]
    [InlineData("run", 1, "")]
    [InlineData("fallback", 1, "")]
    [InlineData("snapshot", 1, "")]
    [InlineData("method", 1, "")]
    [InlineData("type", 1, "")]
    [InlineData("callers", 1, "")]
    [InlineData("references", 1, "")]
    [InlineData("read", 1, "")]
    [InlineData("callees", 1, "")]
    [InlineData("report", 1, "")]
    [InlineData("mcp", 1, "Usage")]
    public async Task Dispatch_InvokesCommandInTemporaryRepository(string command, int expectedExitCode, string outputFragment)
    {
        await ProcessStateLock.WaitAsync();
        var originalDirectory = Environment.CurrentDirectory;
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var root = Directory.CreateTempSubdirectory("devx-cli-dispatch").FullName;
        await File.WriteAllTextAsync(Path.Combine(root, "Sample.cs"), "namespace Sample; public sealed class SampleType { public void Run() { } }");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        try
        {
            Environment.CurrentDirectory = root;
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var entryPoint = typeof(ArtifactCommand).Assembly.EntryPoint!;
            var invocation = entryPoint.Invoke(null, [new[] { command }]);
            var exitCode = invocation is Task<int> task ? await task : Convert.ToInt32(invocation);
            Assert.Equal(expectedExitCode, exitCode);
            var output = stdout.ToString() + stderr.ToString();
            Assert.False(string.IsNullOrWhiteSpace(output), $"'{command}' produced no output.");
            if (!string.IsNullOrEmpty(outputFragment)) Assert.Contains(outputFragment, output, StringComparison.OrdinalIgnoreCase);
            if (command == "health") Assert.Contains("git: Degraded", output, StringComparison.OrdinalIgnoreCase);
            if (command == "doctor") Assert.Contains(".git directory", output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            Environment.CurrentDirectory = originalDirectory;
            Directory.Delete(root, recursive: true);
            ProcessStateLock.Release();
        }
    }

    [Fact]
    public async Task Snapshot_AcceptsSessionOptionBeforeOrAfterFile()
    {
        await ProcessStateLock.WaitAsync();
        var originalDirectory = Environment.CurrentDirectory;
        var root = Directory.CreateTempSubdirectory("devx-snapshot-order").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "Sample.cs"), "public sealed class Sample { }");
            Environment.CurrentDirectory = root;
            var (indexCode, _) = await InvokeCliAsync("index", ".");
            Assert.Equal(0, indexCode);
            var (sessionCode, sessionOutput) = await InvokeCliAsync("session", "create", "--format", "json");
            Assert.Equal(0, sessionCode);
            using var document = JsonDocument.Parse(sessionOutput);
            var sessionId = document.RootElement.GetProperty("Data").GetProperty("sessionId").GetString()!;

            var (fileFirstCode, fileFirst) = await InvokeCliAsync("snapshot", "Sample.cs", "--session", sessionId, "--format", "json");
            var (optionFirstCode, optionFirst) = await InvokeCliAsync("snapshot", "--session", sessionId, "Sample.cs", "--format", "json");

            Assert.Equal(0, fileFirstCode);
            Assert.Equal(0, optionFirstCode);
            using var firstDocument = JsonDocument.Parse(fileFirst);
            using var secondDocument = JsonDocument.Parse(optionFirst);
            Assert.Equal(firstDocument.RootElement.GetRawText(), secondDocument.RootElement.GetRawText());
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Directory.Delete(root, recursive: true);
            ProcessStateLock.Release();
        }
    }

    private static async Task<(int ExitCode, string Output)> InvokeCliAsync(params string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
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
        }
    }
}
