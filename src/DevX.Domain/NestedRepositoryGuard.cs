namespace DevX.Domain;

/// <summary>
/// Finds directories below a repository root that belong to another git worktree or repository
/// (they contain a `.git` file or directory, or are listed by `git worktree list`). Their files
/// are not this repository's files, so indexing and content search skip them.
/// </summary>
public static class NestedRepositoryGuard
{
    private static readonly HashSet<string> PrunedNames = new(StringComparer.Ordinal)
    { ".git", ".devx", "node_modules", ".venv", "venv", "__pycache__", "bin", "obj", "site-packages" };

    /// <summary>True when the directory is the root of another worktree or repository.</summary>
    public static bool IsNestedRepositoryRoot(string directory)
    {
        var marker = Path.Combine(directory, ".git");
        return Directory.Exists(marker) || System.IO.File.Exists(marker);
    }

    /// <summary>Root-relative, '/'-separated nested repository directories (no trailing slash), sorted.</summary>
    public static IReadOnlyList<string> Find(string repositoryRoot)
    {
        var root = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var found = new SortedSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(root)) return [];

        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(directory).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var child in children)
            {
                if (PrunedNames.Contains(Path.GetFileName(child))) continue;
                try { if (new DirectoryInfo(child).LinkTarget is not null) continue; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                if (IsNestedRepositoryRoot(child)) { found.Add(Relative(root, child)); continue; }
                pending.Push(child);
            }
        }

        foreach (var worktree in ListWorktrees(root))
        {
            var full = Path.GetFullPath(worktree).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var prefix = root + Path.DirectorySeparatorChar;
            if (full.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                && Directory.Exists(full))
                found.Add(Relative(root, full));
        }
        return found.ToList();
    }

    /// <summary>True when the root-relative path lies inside one of the excluded directories.</summary>
    public static bool IsInside(string relativePath, IReadOnlyList<string> excluded)
    {
        var normalized = relativePath.Replace('\\', '/');
        foreach (var dir in excluded)
            if (normalized.Length > dir.Length && normalized.StartsWith(dir, StringComparison.Ordinal) && normalized[dir.Length] == '/')
                return true;
        return false;
    }

    private static string Relative(string root, string full)
        => Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');

    private static IEnumerable<string> ListWorktrees(string root)
    {
        var lines = new List<string>();
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            foreach (var arg in new[] { "worktree", "list", "--porcelain" }) start.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(start);
            if (process is null) return lines;
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            _ = error.GetAwaiter().GetResult();
            if (process.ExitCode != 0) return lines;
            foreach (var line in output.Split('\n'))
                if (line.StartsWith("worktree ", StringComparison.Ordinal)) lines.Add(line["worktree ".Length..].TrimEnd('\r'));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { }
        return lines;
    }
}
