using System.Text.Json;
using IOFile = System.IO.File;

namespace Ecp.Cli;

public static class ArtifactCommand
{
    public static async Task<int> RunAsync(string[] args, string format)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: ecp artifact create <name> --content <text>|--content-stdin|--content-file <path>|--content-json <json-string>\necp artifact delete <name>\necp artifact path <name>");
            return 1;
        }

        var command = args[0];
        var name = args.Length > 1 ? args[1] : null;
        var artifactDirectory = Path.Combine(Environment.CurrentDirectory, ".ecp", "artifacts");
        Directory.CreateDirectory(artifactDirectory);

        return command switch
        {
            "create" => await CreateAsync(name, args.Skip(2).ToArray(), artifactDirectory, format),
            "delete" => Delete(name, artifactDirectory, format),
            "path" => PathCommand(name, artifactDirectory, format),
            _ => Fail("Unknown artifact command", format)
        };
    }

    private static async Task<int> CreateAsync(string? name, string[] args, string artifactDirectory, string format)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            Console.Error.WriteLine("Missing artifact name.");
            return 1;
        }

        string? content = null;
        if (args.Length > 0 && args[0] == "--content")
        {
            content = args.Length > 1 ? string.Join(" ", args.Skip(1)) : string.Empty;
        }
        else if (args.Length > 0 && args[0] == "--content-stdin")
        {
            content = await Console.In.ReadToEndAsync();
        }
        else if (args.Length > 1 && args[0] == "--content-file")
        {
            var path = args[1];
            if (!IOFile.Exists(path))
            {
                Console.Error.WriteLine($"Artifact input file '{path}' was not found.");
                return 1;
            }

            content = await IOFile.ReadAllTextAsync(path);
        }
        else if (args.Length > 1 && args[0] == "--content-json")
        {
            try
            {
                content = JsonSerializer.Deserialize<string>(args[1]);
            }
            catch (JsonException)
            {
                Console.Error.WriteLine("Artifact content-json must be a valid JSON string literal.");
                return 1;
            }
        }
        else
        {
            Console.Error.WriteLine("Usage: ecp artifact create <name> --content <text>|--content-stdin|--content-file <path>|--content-json <json-string>");
            return 1;
        }

        var artifactPath = Path.Combine(artifactDirectory, name);
        await IOFile.WriteAllTextAsync(artifactPath, content ?? string.Empty);
        if (format == "json")
        {
            Console.WriteLine($"{{\"created\":true,\"path\":\"{artifactPath}\"}}");
        }
        else
        {
            Console.WriteLine($"Artifact created at {artifactPath}");
        }

        return 0;
    }

    private static int Delete(string? name, string artifactDirectory, string format)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            Console.Error.WriteLine("Missing artifact name.");
            return 1;
        }

        var artifactPath = Path.Combine(artifactDirectory, name);
        if (File.Exists(artifactPath))
        {
            File.Delete(artifactPath);
        }

        if (format == "json")
        {
            Console.WriteLine($"{{\"deleted\":true,\"path\":\"{artifactPath}\"}}");
        }
        else
        {
            Console.WriteLine($"Artifact deleted at {artifactPath}");
        }

        return 0;
    }

    private static int PathCommand(string? name, string artifactDirectory, string format)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            Console.Error.WriteLine("Missing artifact name.");
            return 1;
        }

        var artifactPath = Path.Combine(artifactDirectory, name);
        if (format == "json")
        {
            Console.WriteLine($"{{\"path\":\"{artifactPath}\"}}");
        }
        else
        {
            Console.WriteLine(artifactPath);
        }

        return 0;
    }

    private static int Fail(string message, string format)
    {
        if (format == "json")
        {
            Console.WriteLine($"{{\"error\":\"{message}\"}}");
        }
        else
        {
            Console.Error.WriteLine(message);
        }

        return 1;
    }
}
