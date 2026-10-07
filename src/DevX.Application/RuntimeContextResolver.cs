namespace DevX.Application;

public sealed class RuntimeContextResolver
{
    public RuntimeDiscovery Discover(string repositoryRoot)
    {
        var normalizedRoot = Path.GetFullPath(repositoryRoot);
        if (!Directory.Exists(normalizedRoot))
        {
            return new RuntimeDiscovery(normalizedRoot, []);
        }

        var targets = new List<RuntimeTargetDescriptor>();

        foreach (var solution in Directory.EnumerateFiles(normalizedRoot, "*.sln", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal))
        {
            targets.Add(new RuntimeTargetDescriptor(
                RuntimeKind: "dotnet",
                Label: Path.GetFileName(solution),
                ProjectPath: Path.GetRelativePath(normalizedRoot, solution),
                AvailabilityStatus: "Healthy",
                Prerequisites: [new RuntimePrerequisite("workspace", "Satisfied", "Workspace root must exist.")]));
        }

        foreach (var project in Directory.EnumerateFiles(normalizedRoot, "*.csproj", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal))
        {
            var projectName = Path.GetFileNameWithoutExtension(project);
            var isTestProject = projectName.Contains("Test", StringComparison.OrdinalIgnoreCase);
            targets.Add(new RuntimeTargetDescriptor(
                RuntimeKind: "dotnet",
                Label: isTestProject ? $"{projectName} test project" : $"{projectName} project",
                ProjectPath: Path.GetRelativePath(normalizedRoot, project),
                AvailabilityStatus: "Healthy",
                Prerequisites: [new RuntimePrerequisite("workspace", "Satisfied", "Workspace root must exist.")]));
        }

        foreach (var packageJson in Directory.EnumerateFiles(normalizedRoot, "package.json", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal))
        {
            targets.Add(new RuntimeTargetDescriptor(
                RuntimeKind: "web",
                Label: Path.GetFileName(Path.GetDirectoryName(packageJson) ?? packageJson),
                ProjectPath: Path.GetRelativePath(normalizedRoot, packageJson),
                AvailabilityStatus: "Healthy",
                Prerequisites: [new RuntimePrerequisite("workspace", "Satisfied", "Workspace root must exist.")]));
        }

        // Python has no project file, so anchor on the repository root when it looks like a
        // Python project. Only added when no .csproj/.sln was found, so C# repos that happen to
        // contain .py helper scripts keep their existing dotnet target selection unchanged.
        if (!targets.Any(target => target.RuntimeKind.Equals("dotnet", StringComparison.OrdinalIgnoreCase)))
        {
            var markers = new[] { "pyproject.toml", "setup.py", "setup.cfg", "requirements.txt", "pytest.ini", "tox.ini" };
            var hasMarker = markers.Any(marker => System.IO.File.Exists(Path.Combine(normalizedRoot, marker)));
            var hasPython = hasMarker
                || Directory.EnumerateFiles(normalizedRoot, "*.py", SearchOption.AllDirectories)
                    .Any(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .Any(segment => segment is ".venv" or "venv" or "__pycache__" or "site-packages" or "node_modules"));

            if (hasPython)
            {
                var venv = Path.Combine(normalizedRoot, ".venv", "bin", "python");
                targets.Add(new RuntimeTargetDescriptor(
                    RuntimeKind: "python",
                    Label: $"{Path.GetFileName(normalizedRoot)} (python)",
                    ProjectPath: ".",
                    AvailabilityStatus: "Healthy",
                    Prerequisites:
                    [
                        new RuntimePrerequisite("workspace", "Satisfied", "Workspace root must exist."),
                        new RuntimePrerequisite(
                            "interpreter",
                            System.IO.File.Exists(venv) ? "Satisfied" : "Degraded",
                            System.IO.File.Exists(venv) ? "Using .venv/bin/python." : "No .venv found; falling back to python3.")
                    ]));
            }
        }

        return new RuntimeDiscovery(normalizedRoot, targets);
    }

    public RuntimeContextResolution Resolve(string repositoryRoot, string? runtimeSelector, string? targetSelector, ValidationMode validationMode)
    {
        var discovery = Discover(repositoryRoot);
        var candidates = discovery.Targets.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(runtimeSelector))
        {
            candidates = candidates.Where(target => target.RuntimeKind.Equals(runtimeSelector, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(targetSelector))
        {
            candidates = candidates.Where(target => TargetMatches(discovery.RepositoryRoot, target, targetSelector));
        }
        else if (validationMode == ValidationMode.Test)
        {
            var preferredTests = candidates.Where(target =>
                target.RuntimeKind.Equals("python", StringComparison.OrdinalIgnoreCase)
                || target.ProjectPath?.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) == true
                || target.ProjectPath?.Contains("Test", StringComparison.OrdinalIgnoreCase) == true).ToArray();
            if (preferredTests.Length > 0)
            {
                candidates = preferredTests;
            }
        }
        else
        {
            var preferredBuild = candidates.Where(target =>
                target.RuntimeKind.Equals("python", StringComparison.OrdinalIgnoreCase)
                || target.ProjectPath?.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) == true
                || (target.ProjectPath?.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) == true
                    && !target.ProjectPath.Contains("Test", StringComparison.OrdinalIgnoreCase))).ToArray();
            if (preferredBuild.Length > 0)
            {
                candidates = preferredBuild;
            }
        }

        var materialized = candidates.ToArray();
        if (materialized.Length == 0)
        {
            return new RuntimeContextResolution(
                false,
                null,
                null,
                null,
                ["No runtime target matched the requested runtime/project selection."],
                discovery.Targets);
        }

        var selected = materialized[0];
        var runtimeFamily = selected.RuntimeKind;
        var context = new RuntimeContext(
            WorkspaceRoot: discovery.RepositoryRoot,
            RepositoryRoot: discovery.RepositoryRoot,
            ProjectPath: selected.ProjectPath,
            RuntimeFamily: runtimeFamily,
            ConfigurationProfile: "default");

        return new RuntimeContextResolution(true, context, selected, selected.ProjectPath, [], discovery.Targets);
    }

    private static bool TargetMatches(string repositoryRoot, RuntimeTargetDescriptor target, string selector)
    {
        var projectPath = target.ProjectPath ?? string.Empty;
        if (projectPath.Equals(selector, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var fullPath = Path.GetFullPath(Path.Combine(repositoryRoot, projectPath));
        if (fullPath.Equals(Path.GetFullPath(Path.IsPathRooted(selector) ? selector : Path.Combine(repositoryRoot, selector)), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var fileName = Path.GetFileName(projectPath);
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(projectPath);
        return fileName.Equals(selector, StringComparison.OrdinalIgnoreCase)
            || fileNameWithoutExtension.Equals(selector, StringComparison.OrdinalIgnoreCase)
            || projectPath.Contains(selector, StringComparison.OrdinalIgnoreCase);
    }
}
