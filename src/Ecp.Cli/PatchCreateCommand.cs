using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ecp.Domain;
using Ecp.Infrastructure.Storage.SQLite;
using IOFile = System.IO.File;

namespace Ecp.Cli;

/// <summary>
/// Implements <c>ecp patch create</c>: authors a standard ECP patch artifact
/// (version + metadata + operations) so repository changes can be described
/// through ECP instead of direct file editing. The produced artifact is
/// consumed unchanged by <c>patch preview|validate|apply|rollback</c>; the
/// extra <c>metadata</c> object is ignored by the patch deserializer.
/// </summary>
public static class PatchCreateCommand
{
    private static readonly string[] StdinFlags = ["--content-stdin", "--replacement-stdin", "--method-text-stdin"];
    private static readonly string[] FileFlags = ["--content-file", "--replacement-file", "--method-text-file"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    public static async Task<int> RunAsync(string connectionString, string[] args, string format)
    {
        var outputPath = GetPatchPath(args);
        var sessionId = GetOption(args, "--session");
        if (sessionId is null)
        {
            Console.Error.WriteLine("Missing --session.");
            return 1;
        }

        if (outputPath is null)
        {
            Console.Error.WriteLine("Usage: ecp patch create <output-file> --session <id> --op <Operation> --path <relative-path> [operation options] [--append]");
            return 1;
        }

        if (!Guid.TryParse(sessionId, out var sessionGuid))
        {
            Console.Error.WriteLine("Invalid --session identifier.");
            return 1;
        }

        var operationName = GetOption(args, "--op");
        if (string.IsNullOrWhiteSpace(operationName))
        {
            Console.Error.WriteLine("Missing --op. Supported: CreateFile, DeleteFile, ReplaceMethod, InsertMethod, AddUsing, InsertText, ReplaceLineRange.");
            return 1;
        }

        var operationPath = GetOption(args, "--path");
        if (string.IsNullOrWhiteSpace(operationPath))
        {
            Console.Error.WriteLine("Missing --path.");
            return 1;
        }

        var stdinFlags = StdinFlags.Where(flag => HasFlag(args, flag)).ToArray();
        if (stdinFlags.Length > 1)
        {
            Console.Error.WriteLine($"Only one stdin source may be used per operation. Got: {string.Join(", ", stdinFlags)}.");
            return 1;
        }

        var stdinContent = stdinFlags.Length == 1 ? await Console.In.ReadToEndAsync() : null;
        var content = await ResolveFieldAsync(GetOption(args, "--content"), HasFlag(args, "--content-stdin"), stdinContent, GetOption(args, "--content-file"));
        var replacementText = await ResolveFieldAsync(GetOption(args, "--replacement"), HasFlag(args, "--replacement-stdin"), stdinContent, GetOption(args, "--replacement-file"));
        var methodText = await ResolveFieldAsync(GetOption(args, "--method-text"), HasFlag(args, "--method-text-stdin"), stdinContent, GetOption(args, "--method-text-file"));

        var beforeState = await TryReadBeforeStateAsync(connectionString, sessionGuid, operationPath, operationName);
        var operation = new PatchOperation(
            Operation: operationName,
            Path: operationPath,
            Content: content,
            MethodName: GetOption(args, "--method"),
            ReplacementText: replacementText,
            ContainingType: GetOption(args, "--containing-type"),
            TypeId: GetOption(args, "--type-id"),
            MethodText: methodText,
            Position: GetOption(args, "--position"),
            Namespace: GetOption(args, "--namespace"),
            BeforeState: beforeState);

        var operations = new List<PatchOperation>();
        var appending = HasFlag(args, "--append");
        if (appending && IOFile.Exists(outputPath))
        {
            try
            {
                var existingJson = await IOFile.ReadAllTextAsync(outputPath);
                var existing = JsonSerializer.Deserialize<PatchDocument>(existingJson, JsonOptions);
                if (existing?.Operations is not null)
                {
                    operations.AddRange(existing.Operations);
                }
            }
            catch (JsonException)
            {
                Console.Error.WriteLine($"Cannot append: '{outputPath}' is not a valid patch artifact.");
                return 1;
            }
        }

        operations.Add(operation);

        string? repositoryId = null;
        try
        {
            var store = new SqliteStorageInitializer(connectionString);
            await store.InitializeAsync();
            var session = await store.GetSessionAsync(new GetSessionRequest(new SessionId(sessionGuid)));
            repositoryId = session?.RepositoryId.Value.ToString();
        }
        catch
        {
            repositoryId = null;
        }

        var operationHashes = operations.Select(HashOperation).ToArray();
        var affectedFiles = operations
            .Select(op => op.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var metadata = new PatchArtifactMetadata(
            PatchId: Guid.NewGuid().ToString(),
            CreatedUtc: DateTimeOffset.UtcNow.ToString("O"),
            SessionId: sessionId,
            RepositoryId: repositoryId,
            AffectedFiles: affectedFiles,
            OperationHashes: operationHashes);

        var artifact = new PatchArtifact("1.0", metadata, operations);
        var json = JsonSerializer.Serialize(artifact, JsonOptions);

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await IOFile.WriteAllTextAsync(outputPath, json);

        if (format == "json")
        {
            var result = new
            {
                Created = true,
                Path = outputPath,
                metadata.PatchId,
                Operations = operations.Count,
                AffectedFiles = affectedFiles,
                Appended = appending
            };
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        }
        else
        {
            Console.WriteLine($"Patch artifact written to {outputPath}");
            Console.WriteLine($"Patch id: {metadata.PatchId}");
            Console.WriteLine($"Operations: {operations.Count}");
            Console.WriteLine($"Affected files: {string.Join(", ", affectedFiles)}");
        }

        return 0;
    }

    private static async Task<string?> ResolveFieldAsync(string? inlineValue, bool useStdin, string? stdinContent, string? filePath)
    {
        if (useStdin)
        {
            return stdinContent;
        }

        if (!string.IsNullOrWhiteSpace(filePath))
        {
            if (!IOFile.Exists(filePath))
            {
                throw new FileNotFoundException($"Input file '{filePath}' was not found.");
            }

            return await IOFile.ReadAllTextAsync(filePath);
        }

        return inlineValue;
    }

    private static async Task<string?> TryReadBeforeStateAsync(string connectionString, Guid sessionGuid, string operationPath, string operationName)
    {
        if (!operationName.Equals("ReplaceMethod", StringComparison.OrdinalIgnoreCase)
            && !operationName.Equals("InsertMethod", StringComparison.OrdinalIgnoreCase)
            && !operationName.Equals("ReplaceLineRange", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var store = new SqliteStorageInitializer(connectionString);
            await store.InitializeAsync();
            var session = await store.GetSessionAsync(new GetSessionRequest(new SessionId(sessionGuid)));
            if (session is null)
            {
                return null;
            }

            var repositoryRoot = await store.GetRepositoryRootPathAsync(session.RepositoryId);
            if (string.IsNullOrWhiteSpace(repositoryRoot))
            {
                return null;
            }

            var fullPath = Path.GetFullPath(Path.Combine(repositoryRoot, operationPath));
            return IOFile.Exists(fullPath) ? await IOFile.ReadAllTextAsync(fullPath) : null;
        }
        catch
        {
            return null;
        }
    }

    private static string HashOperation(PatchOperation operation)
    {
        var canonical = JsonSerializer.Serialize(operation, JsonOptions);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string? GetOption(string[] args, string name)
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

    private static bool HasFlag(string[] args, string name)
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

    private static string? GetPatchPath(string[] args)
    {
        for (var i = 2; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    i++;
                }

                continue;
            }

            return args[i];
        }

        return null;
    }
}

public sealed record PatchArtifact(string Version, PatchArtifactMetadata Metadata, IReadOnlyList<PatchOperation> Operations);

public sealed record PatchArtifactMetadata(
    string PatchId,
    string CreatedUtc,
    string SessionId,
    string? RepositoryId,
    IReadOnlyList<string> AffectedFiles,
    IReadOnlyList<string> OperationHashes);
