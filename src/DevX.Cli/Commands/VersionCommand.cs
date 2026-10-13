using System.Text.Json;
using System.Reflection;
using DevX.Application;
using DevX.Domain;
using DevX.Cli;
using DevX.Infrastructure.Storage.SQLite;
using DevX.Indexer;
using DevX.Protocol;

static partial class Program
{
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
}
