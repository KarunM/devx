using System.Text.Json;
using DevX.Application;
using DevX.Domain;
using DevX.Cli;
using DevX.Infrastructure.Storage.SQLite;
using DevX.Indexer;
using DevX.Protocol;

static partial class Program
{
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
}
