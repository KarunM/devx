using System.IO;

namespace Ecp.Cli;

public sealed class SandboxManager : IDisposable
{
    private readonly string _sandboxRoot;
    private readonly string _originalRoot;
    private bool _disposed;

    public string SandboxRoot => _sandboxRoot;
    public string OriginalRoot => _originalRoot;

    public SandboxManager(string originalRoot)
    {
        _originalRoot = Path.GetFullPath(originalRoot);
        _sandboxRoot = Path.Combine(Path.GetTempPath(), $"ecp-sandbox-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_sandboxRoot);
    }

    /// <summary>
    /// Copies the source tree into the sandbox, excluding ignored directories.
    /// </summary>
    public void CopySourceTree()
    {
        CopyDirectory(_originalRoot, _sandboxRoot);
    }

    /// <summary>
    /// Copies changed files from sandbox back to the original repository.
    /// </summary>
    public void ApplyChanges()
    {
        foreach (var sandboxFile in EnumerateFiles(_sandboxRoot))
        {
            var relativePath = Path.GetRelativePath(_sandboxRoot, sandboxFile);
            var originalFile = Path.Combine(_originalRoot, relativePath);
            var originalDir = Path.GetDirectoryName(originalFile)!;
            Directory.CreateDirectory(originalDir);
            File.Copy(sandboxFile, originalFile, overwrite: true);
        }
    }

    /// <summary>
    /// Returns a list of files that differ between sandbox and original.
    /// </summary>
    public IReadOnlyList<string> GetChangedFiles()
    {
        var changed = new List<string>();
        foreach (var sandboxFile in EnumerateFiles(_sandboxRoot))
        {
            var relativePath = Path.GetRelativePath(_sandboxRoot, sandboxFile);
            var originalFile = Path.Combine(_originalRoot, relativePath);
            if (!File.Exists(originalFile))
            {
                changed.Add(relativePath + " (new)");
                continue;
            }
            var sandboxContent = File.ReadAllText(sandboxFile);
            var originalContent = File.ReadAllText(originalFile);
            if (sandboxContent != originalContent)
            {
                changed.Add(relativePath);
            }
        }
        return changed;
    }

    /// <summary>
    /// Returns a unified diff of all changed files.
    /// </summary>
    public string GetUnifiedDiff(int contextLines = 3)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var sandboxFile in EnumerateFiles(_sandboxRoot))
        {
            var relativePath = Path.GetRelativePath(_sandboxRoot, sandboxFile);
            var originalFile = Path.Combine(_originalRoot, relativePath);
            if (!File.Exists(originalFile))
            {
                sb.AppendLine($"--- /dev/null");
                sb.AppendLine($"+++ {relativePath}");
                var lines = File.ReadAllLines(sandboxFile);
                sb.AppendLine($"@@ -0,0 +1,{lines.Length} @@");
                foreach (var line in lines)
                {
                    sb.AppendLine($"+{line}");
                }
                sb.AppendLine();
                continue;
            }
            var originalLines = File.ReadAllLines(originalFile);
            var sandboxLines = File.ReadAllLines(sandboxFile);
            if (originalLines.SequenceEqual(sandboxLines))
            {
                continue;
            }
            sb.AppendLine($"--- {relativePath}");
            sb.AppendLine($"+++ {relativePath}");
            // Simple line-by-line diff
            var maxLen = Math.Max(originalLines.Length, sandboxLines.Length);
            for (var i = 0; i < maxLen; i++)
            {
                var orig = i < originalLines.Length ? originalLines[i] : null;
                var sand = i < sandboxLines.Length ? sandboxLines[i] : null;
                if (orig != sand)
                {
                    if (orig != null) sb.AppendLine($"-{orig}");
                    if (sand != null) sb.AppendLine($"+{sand}");
                }
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (Directory.Exists(_sandboxRoot))
            {
                Directory.Delete(_sandboxRoot, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    private static void CopyDirectory(string source, string dest)
    {
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".ecp", "bin", "obj", ".vs", ".idea", "node_modules", "dist", "out", "coverage"
        };

        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(dir);
            if (ignored.Contains(name)) continue;
            var destDir = Path.Combine(dest, name);
            Directory.CreateDirectory(destDir);
            CopyDirectory(dir, destDir);
        }

        foreach (var file in Directory.EnumerateFiles(source))
        {
            var name = Path.GetFileName(file);
            if (ignored.Contains(name)) continue;
            var destFile = Path.Combine(dest, name);
            File.Copy(file, destFile, overwrite: true);
        }
    }

    private static IEnumerable<string> EnumerateFiles(string root)
    {
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".ecp", "bin", "obj", ".vs", ".idea", "node_modules", "dist", "out", "coverage"
        };

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var parts = file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Any(p => ignored.Contains(p))) continue;
            yield return file;
        }
    }
}
