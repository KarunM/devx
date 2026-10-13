using System.Text.Json;
using DevX.Application;
using DevX.Domain;
using DevX.Cli;
using DevX.Infrastructure.Storage.SQLite;
using DevX.Indexer;
using DevX.Protocol;

static partial class Program
{
    static bool HasFlag(string[] args, string name)
    {
        foreach (var arg in args)
        {
            if (arg == name)
            {
                return true;
            }
        }

        return false;
    }

    static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        }
        value = default;
        return false;
    }

    static string JsonPropertyText(JsonElement element, string name)
        => TryGetPropertyIgnoreCase(element, name, out var value) ? value.ToString() : string.Empty;

    static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }

        return null;
    }

    static IReadOnlyList<string> GetPositionals(string[] args, int startIndex)
    {
        var values = new List<string>();
        for (var index = startIndex; index < args.Length; index++)
        {
            if (args[index].StartsWith("--", StringComparison.Ordinal))
            {
                if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)) index++;
                continue;
            }
            values.Add(args[index]);
        }
        return values;
    }

    static string? GetAgentLabel(string[] args)
        => GetOption(args, "--agent")
           ?? Environment.GetEnvironmentVariable("DEVX_AGENT")
           ?? Environment.GetEnvironmentVariable("CODEX_AGENT_NAME");

    static string ResolveDatabasePath()
    {
        var stateDirectory = Path.Combine(Environment.CurrentDirectory, ".devx");
        var databasePath = Path.Combine(stateDirectory, "devx.db");
        Directory.CreateDirectory(stateDirectory);

        return databasePath;
    }

    static string ResolveFormat(string[] args)
        => OutputFormat.Resolve(GetOption(args, "--format"), OutputFormat.LooksLikeAnAgent());
}
