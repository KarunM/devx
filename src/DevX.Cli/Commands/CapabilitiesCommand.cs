using System.Text.Json;
using DevX.Application;
using DevX.Domain;
using DevX.Cli;
using DevX.Infrastructure.Storage.SQLite;
using DevX.Indexer;
using DevX.Protocol;
using Microsoft.Data.Sqlite;

static partial class Program
{
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
}
