using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DevX.Infrastructure.Storage.SQLite;

/// <summary>Which ripgrep `devx search text` is using, if any.</summary>
public enum RipgrepEngine
{
    /// <summary>No ripgrep anywhere; the in-process regex scan answers.</summary>
    None,

    /// <summary>`rg.path` in `.devx/config.json`.</summary>
    Configured,

    /// <summary>Shipped with this DevX (release artifact or tool package).</summary>
    Bundled,

    /// <summary>Found on PATH.</summary>
    Path,
}

/// <summary>The resolved engine, its binary and its version, for the doctor and the caller.</summary>
public sealed record RipgrepLocation(string? Executable, RipgrepEngine Engine, string? Version)
{
    public string Describe() => Engine switch
    {
        RipgrepEngine.Configured => $"ripgrep {Version ?? "unknown"} (configured: {Executable})",
        RipgrepEngine.Bundled => $"ripgrep {Version ?? "unknown"} (bundled)",
        RipgrepEngine.Path => $"ripgrep {Version ?? "unknown"} (on PATH)",
        _ => "built-in regex scan (no ripgrep installed)",
    };
}

/// <summary>
/// Finds the ripgrep binary `devx search text` should use, in this order:
///
///   1. `.devx/config.json` -> `"rg": { "path": "..." }` (a repository that wants its own rg)
///   2. the copy shipped with this DevX (next to the binary, or in the tool package)
///   3. `rg` on PATH
///   4. nothing -- the in-process regex scan in <see cref="SqliteStorageInitializer"/> answers
///
/// Bundling is the point: `rg` is not installed on a stock machine, and the 1.6.12 release
/// only reached ripgrep when the machine happened to have it. The fallback stays, so no call
/// fails without it -- but the engine that respects .gitignore and searches the tree rather
/// than the index is the one that should normally answer.
/// </summary>
public static class RipgrepLocator
{
    private static readonly ConcurrentDictionary<string, string?> Versions = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string?> PathHits = new(StringComparer.Ordinal);

    public static string FileName() => OperatingSystem.IsWindows() ? "rg.exe" : "rg";

    /// <summary>The runtime identifier of the rg copy this build would ship, matching the
    /// release RIDs (osx-arm64, osx-x64, linux-x64, linux-arm64, win-x64).</summary>
    public static string RuntimeIdentifier()
    {
        var architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            _ => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
        };
        if (OperatingSystem.IsWindows()) return $"win-{architecture}";
        if (OperatingSystem.IsMacOS()) return $"osx-{architecture}";
        return $"linux-{architecture}";
    }

    public static RipgrepLocation Locate(string? repositoryRoot = null)
        => Locate(repositoryRoot, AppContext.BaseDirectory);

    public static RipgrepLocation Locate(string? repositoryRoot, string baseDirectory)
    {
        var configured = ConfiguredPath(repositoryRoot);
        if (configured is not null)
        {
            return new RipgrepLocation(configured, RipgrepEngine.Configured, VersionOf(configured));
        }

        var bundled = BundledPath(baseDirectory);
        if (bundled is not null)
        {
            return new RipgrepLocation(bundled, RipgrepEngine.Bundled, VersionOf(bundled));
        }

        var onPath = PathHit();
        if (onPath is not null)
        {
            return new RipgrepLocation(FileName(), RipgrepEngine.Path, VersionOf(onPath));
        }

        return new RipgrepLocation(null, RipgrepEngine.None, null);
    }

    /// <summary>`"rg": { "path": "tools/rg" }` in `.devx/config.json`. A relative path is
    /// relative to the repository root, so one config can be checked in everywhere.</summary>
    public static string? ConfiguredPath(string? repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot)) return null;
        var configPath = Path.Combine(repositoryRoot, ".devx", "config.json");
        if (!File.Exists(configPath)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!document.RootElement.TryGetProperty("rg", out var rg) || rg.ValueKind != JsonValueKind.Object) return null;
            if (!rg.TryGetProperty("path", out var pathElement) || pathElement.ValueKind != JsonValueKind.String) return null;
            var path = pathElement.GetString();
            if (string.IsNullOrWhiteSpace(path)) return null;
            var resolved = Path.IsPathRooted(path) ? path : Path.Combine(repositoryRoot, path);
            return File.Exists(resolved) ? resolved : null;
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>The copy shipped beside the binary, or the per-RID folder of the tool package.</summary>
    public static string? BundledPath(string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory)) return null;
        var name = FileName();
        var candidates = new[]
        {
            Path.Combine(baseDirectory, name),
            Path.Combine(baseDirectory, "rg-tools", name),
            Path.Combine(baseDirectory, "rg-tools", RuntimeIdentifier(), name),
            Path.Combine(baseDirectory, "runtimes", RuntimeIdentifier(), "native", name),
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>An `rg` on PATH, or null. Cached per PATH value, because the doctor and every
    /// search text call ask and the answer only changes when PATH does.</summary>
    public static string? PathHit()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return PathHits.GetOrAdd(path, value =>
        {
            foreach (var directory in value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    var candidate = Path.Combine(directory, FileName());
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException)
                {
                    // a malformed PATH entry is not a reason to stop looking
                }
            }

            return null;
        });
    }

    /// <summary>The first line of `rg --version` (`ripgrep 15.2.0 (rev ...)`), or null when the
    /// binary cannot be run. Cached: this starts a process.</summary>
    public static string? VersionOf(string executable)
        => Versions.GetOrAdd(executable, name =>
        {
            try
            {
                var start = new ProcessStartInfo(name)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                start.ArgumentList.Add("--version");
                using var process = Process.Start(start);
                if (process is null) return null;
                var line = process.StandardOutput.ReadLine();
                process.WaitForExit(5_000);
                if (string.IsNullOrWhiteSpace(line)) return null;
                line = line.Trim();
                // `rg --version` answers `ripgrep 15.2.0 (rev ...)`. The name is implied; keep
                // the version and revision so `ripgrep 15.2.0 (rev ...) (bundled)` reads right.
                return line.StartsWith("ripgrep ", StringComparison.OrdinalIgnoreCase) ? line["ripgrep ".Length..] : line;
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                return null;
            }
        });
}
