namespace DevX.Domain;

using IOFile = System.IO.File;

/// <summary>
/// Resolves repository-relative paths without allowing a component below the trusted root
/// to redirect filesystem access through a symbolic link or reparse point.
/// </summary>
public static class RepositoryPathGuard
{
    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public static bool TryResolve(string repositoryRoot, string requestedPath, out string absolutePath, out string error)
    {
        absolutePath = string.Empty;
        error = string.Empty;
        try
        {
            var lexicalRoot = Path.GetFullPath(repositoryRoot);
            if (!TryResolveTrustedRoot(lexicalRoot, out var physicalRoot, out error)) return false;

            string relativePath;
            if (Path.IsPathRooted(requestedPath))
            {
                var requestedFull = Path.GetFullPath(requestedPath);
                if (!TryGetRelativeIfContained(lexicalRoot, requestedFull, out relativePath)
                    && !TryGetRelativeIfContained(physicalRoot, requestedFull, out relativePath))
                {
                    error = $"Path '{requestedPath}' is outside the repository root.";
                    return false;
                }
            }
            else
            {
                var requestedFull = Path.GetFullPath(Path.Combine(lexicalRoot, requestedPath));
                if (!TryGetRelativeIfContained(lexicalRoot, requestedFull, out relativePath))
                {
                    error = $"Path '{requestedPath}' is outside the repository root.";
                    return false;
                }
            }

            if (Path.IsPathRooted(relativePath) || relativePath == ".."
                || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, PathComparison)
                || relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, PathComparison))
            {
                error = $"Path '{requestedPath}' is outside the repository root.";
                return false;
            }

            var physicalTarget = Path.GetFullPath(Path.Combine(physicalRoot, relativePath));
            if (!TryGetRelativeIfContained(physicalRoot, physicalTarget, out _))
            {
                error = $"Path '{requestedPath}' is outside the repository root.";
                return false;
            }

            var current = physicalRoot;
            foreach (var component in relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, component);
                if (IsLinkOrReparsePoint(current))
                {
                    error = $"Path '{requestedPath}' contains a symbolic link or reparse point below the repository root.";
                    return false;
                }

                if (!IOFile.Exists(current) && !Directory.Exists(current)) break;
            }

            // Preserve the registered root spelling so existing repository identity and
            // relative file paths do not change when the root itself is a trusted alias.
            absolutePath = Path.GetFullPath(Path.Combine(lexicalRoot, relativePath));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or PathTooLongException)
        {
            error = $"Path '{requestedPath}' could not be checked safely: {ex.Message}";
            return false;
        }
    }

    /// <summary>Returns files below a repository while never descending into symlink entries.</summary>
    public static IEnumerable<string> EnumerateFiles(string repositoryRoot, string pattern = "*")
    {
        var root = Path.GetFullPath(repositoryRoot);
        if (!TryResolveTrustedRoot(root, out _, out var error)) throw new IOException(error);

        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (IsLinkOrReparsePoint(entry)) continue;
                if (Directory.Exists(entry))
                {
                    pending.Push(entry);
                    continue;
                }

                if (IOFile.Exists(entry)
                    && (pattern == "*" || System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(
                        pattern, Path.GetFileName(entry), OperatingSystem.IsWindows())))
                {
                    yield return entry;
                }
            }
        }
    }

    private static bool TryGetRelativeIfContained(string root, string path, out string relativePath)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(path);
        if (fullPath.Equals(fullRoot, PathComparison))
        {
            relativePath = string.Empty;
            return true;
        }

        var prefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, PathComparison))
        {
            relativePath = string.Empty;
            return false;
        }

        relativePath = Path.GetRelativePath(fullRoot, fullPath);
        return true;
    }

    private static bool TryResolveTrustedRoot(string root, out string physicalRoot, out string error)
    {
        physicalRoot = Path.GetPathRoot(root) ?? string.Empty;
        error = string.Empty;
        if (physicalRoot.Length == 0)
        {
            error = "Repository root could not be resolved.";
            return false;
        }

        var remainder = root[physicalRoot.Length..];
        foreach (var component in remainder.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(physicalRoot, component);
            if (!Directory.Exists(candidate))
            {
                error = $"Repository root does not exist or is not a directory: {root}";
                return false;
            }

            if (IsLinkOrReparsePoint(candidate))
            {
                var target = new DirectoryInfo(candidate).ResolveLinkTarget(returnFinalTarget: true);
                if (target is null)
                {
                    error = $"Trusted repository root alias could not be resolved: {root}";
                    return false;
                }

                physicalRoot = Path.GetFullPath(target.FullName);
            }
            else
            {
                physicalRoot = candidate;
            }
        }

        physicalRoot = Path.GetFullPath(physicalRoot);
        return true;
    }

    private static bool IsLinkOrReparsePoint(string path)
    {
        try
        {
            if ((IOFile.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return true;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }

        try
        {
            if (new FileInfo(path).LinkTarget is not null) return true;
            if (new DirectoryInfo(path).LinkTarget is not null) return true;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        return false;
    }
}
