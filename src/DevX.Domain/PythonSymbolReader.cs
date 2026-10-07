using System.Diagnostics;
using System.Text.Json;

namespace DevX.Domain;

/// <summary>
/// Shared entry point for shelling out to the `python3 -c ast`-style helper script that emits
/// Python symbols (classes, module-level functions, methods) as JSON in the same shape
/// CSharpRoslynParser's ParsedFile/ParsedType/ParsedMethod records use. Lives in DevX.Domain (which
/// has no project dependencies) so both DevX.Indexer (initial indexing) and DevX.Infrastructure
/// (live re-parse for `read method` / `snapshot` / `type`) can call it without a circular reference.
/// </summary>
public static class PythonSymbolReader
{
    private static readonly string ScriptPath = ResolveScriptPath();

    public static bool CanParse(string filePath) => Path.GetExtension(filePath).Equals(".py", StringComparison.OrdinalIgnoreCase);

    public static ParsedFile Parse(string filePath)
    {
        var startInfo = new ProcessStartInfo("python3", $"\"{ScriptPath}\" \"{filePath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start python3.");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
        {
            return new ParsedFile(filePath, null, []);
        }

        var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        var @namespace = ModuleNamespaceFromPath(filePath);

        var types = new List<ParsedType>();
        foreach (var typeElement in root.GetProperty("Types").EnumerateArray())
        {
            var methods = new List<ParsedMethod>();
            foreach (var methodElement in typeElement.GetProperty("Methods").EnumerateArray())
            {
                methods.Add(new ParsedMethod(
                    methodElement.GetProperty("Name").GetString() ?? string.Empty,
                    methodElement.GetProperty("Signature").GetString() ?? string.Empty,
                    methodElement.GetProperty("ReturnType").GetString() ?? string.Empty,
                    methodElement.GetProperty("Visibility").GetString() ?? "public",
                    methodElement.GetProperty("StartLine").GetInt32(),
                    methodElement.GetProperty("EndLine").GetInt32(),
                    methodElement.GetProperty("IsConstructor").GetBoolean()));
            }

            var fields = new List<ParsedField>();
            foreach (var fieldElement in typeElement.GetProperty("Fields").EnumerateArray())
            {
                fields.Add(new ParsedField(
                    fieldElement.GetProperty("Name").GetString() ?? string.Empty,
                    fieldElement.GetProperty("Type").GetString() ?? string.Empty,
                    fieldElement.GetProperty("Visibility").GetString() ?? "public",
                    fieldElement.GetProperty("StartLine").GetInt32(),
                    fieldElement.GetProperty("EndLine").GetInt32()));
            }

            types.Add(new ParsedType(
                typeElement.GetProperty("Name").GetString() ?? string.Empty,
                typeElement.GetProperty("Kind").GetString() ?? "Class",
                @namespace,
                typeElement.GetProperty("Visibility").GetString() ?? "public",
                typeElement.GetProperty("StartLine").GetInt32(),
                typeElement.GetProperty("EndLine").GetInt32(),
                methods,
                [],
                fields));
        }

        return new ParsedFile(filePath, @namespace, types);
    }

    /// <summary>
    /// Synthesizes a dotted module path (e.g. "mesh.pose_in_room") from the file path so Python files
    /// (which have no project/namespace file the way C# has .csproj) still get a non-empty
    /// ParsedFile.Namespace, which the indexing pipeline requires before it will emit any symbols.
    /// </summary>
    public static string ModuleNamespaceFromPath(string filePath)
    {
        var withoutExtension = filePath.EndsWith(".py", StringComparison.OrdinalIgnoreCase)
            ? filePath[..^3]
            : filePath;
        var normalized = withoutExtension.Replace('\\', '/').Trim('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var take = Math.Min(segments.Length, 3);
        return string.Join('.', segments[^take..]);
    }

    private static string ResolveScriptPath()
    {
        return ResolveScriptPath(
            typeof(PythonSymbolReader).Assembly.Location,
            AppContext.BaseDirectory,
            Environment.ProcessPath,
            System.IO.File.Exists);
    }

    internal static string ResolveScriptPath(
        string assemblyLocation,
        string baseDirectory,
        string? processPath,
        Func<string, bool> exists)
    {
        var baseCandidate = Path.Combine(baseDirectory, "PythonAst", "parse_symbols.py");
        var directories = new List<string>();

        if (!string.IsNullOrEmpty(assemblyLocation) && Path.GetDirectoryName(assemblyLocation) is { } assemblyDirectory)
        {
            directories.Add(assemblyDirectory);
        }

        directories.Add(baseDirectory);

        if (!string.IsNullOrEmpty(processPath) && Path.GetDirectoryName(processPath) is { } processDirectory)
        {
            directories.Add(processDirectory);
        }

        foreach (var directory in directories)
        {
            var candidate = Path.Combine(directory, "PythonAst", "parse_symbols.py");
            if (exists(candidate))
            {
                return candidate;
            }
        }

        return baseCandidate;
    }
}
