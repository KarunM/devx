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
}
