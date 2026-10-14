namespace DevX.Application;

public sealed class RuntimeHealthService
{
    private readonly RuntimeContextResolver _resolver = new();

    public Task<RuntimeHealthRecord> EvaluateAsync(
        string repositoryRoot,
        bool repositoryIndexed,
        int indexedFiles,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? indexedPaths = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedRoot = Path.GetFullPath(repositoryRoot);
        var workspaceExists = Directory.Exists(normalizedRoot);
        // In a linked Git worktree, .git is a pointer file rather than a directory.
        var gitMetadataPath = Path.Combine(normalizedRoot, ".git");
        var gitExists = workspaceExists && (Directory.Exists(gitMetadataPath) || IsGitWorktreePointer(gitMetadataPath));
        var discovery = workspaceExists ? _resolver.Discover(normalizedRoot) : new RuntimeDiscovery(normalizedRoot, []);
        var selectedProject = discovery.Targets.FirstOrDefault()?.ProjectPath;
        var runtimeFamily = discovery.Targets.FirstOrDefault()?.RuntimeKind ?? "unknown";

        var checks = new List<RuntimeHealthCheck>
        {
            new(
                "workspace",
                workspaceExists ? "Healthy" : "Unavailable",
                workspaceExists ? "Workspace root is available." : "Workspace root does not exist."),
            new(
                "repository",
                workspaceExists ? "Healthy" : "Unavailable",
                workspaceExists ? "Repository root resolved successfully." : "Repository root could not be resolved."),
            new(
                "index",
                repositoryIndexed && indexedFiles > 0 ? "Healthy" : repositoryIndexed ? "Degraded" : "Unavailable",
                repositoryIndexed
                    ? indexedFiles > 0
                        ? $"Repository index is available with {indexedFiles} indexed file(s)."
                        : "Repository is indexed but no indexed files were reported."
                    : "Repository has not been indexed yet."),
            new(
                "targets",
                selectedProject is not null ? "Healthy" : "Unavailable",
                selectedProject is not null
                    ? $"Discovered runtime target '{Path.GetFileName(selectedProject)}'."
                    : "No .sln, .csproj, or package.json target was discovered."),
            new(
                "git",
                gitExists ? "Healthy" : "Degraded",
                gitExists ? "Git repository metadata is available." : "Git metadata was not found in the workspace root.")
        };

        var blockingConditions = new List<string>();
        if (!workspaceExists)
        {
            blockingConditions.Add("Workspace root does not exist.");
        }

        if (!repositoryIndexed)
        {
            blockingConditions.Add("Repository is not indexed.");
        }

        if (selectedProject is null)
        {
            blockingConditions.Add("No runtime target was discovered.");
        }

        var warnings = new List<string>();
        var missing = (indexedPaths ?? []).Where(path => !System.IO.File.Exists(Path.Combine(normalizedRoot, path))).OrderBy(path => path, StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            var message = $"{missing.Count} indexed files missing on disk: {string.Join(", ", missing)}";
            warnings.Add(message);
            checks.Add(new RuntimeHealthCheck("index-files", "Degraded", message));
        }
        if (repositoryIndexed && indexedFiles == 0)
        {
            warnings.Add("Index exists but contains no indexed files.");
        }

        if (!gitExists && workspaceExists)
        {
            warnings.Add("Workspace does not contain a .git directory at the repository root.");
        }

        if (discovery.Targets.Any(target => target.RuntimeKind == "dotnet") && discovery.Targets.Any(target => target.RuntimeKind == "web"))
        {
            warnings.Add("Workspace contains both .NET and web targets; explicit target selection may be required.");
        }

        var remediations = new List<string>();
        if (!repositoryIndexed)
        {
            remediations.Add("Run `devx index <path>` before using runtime validation and discovery commands.");
        }
        if (missing.Count > 0)
        {
            remediations.Add("run 'devx index .' to remove them");
        }

        if (selectedProject is null)
        {
            remediations.Add("Add or select a supported runtime target such as a .sln, .csproj, or package.json.");
        }

        if (!gitExists && workspaceExists)
        {
            remediations.Add("Initialize or open the repository from its Git root to enable repository-scoped workflows.");
        }

        var status = blockingConditions.Count > 0
            ? "Unavailable"
            : warnings.Count > 0
                ? "Degraded"
                : "Healthy";

        var context = new RuntimeContext(
            WorkspaceRoot: normalizedRoot,
            RepositoryRoot: normalizedRoot,
            ProjectPath: selectedProject,
            RuntimeFamily: runtimeFamily,
            ConfigurationProfile: "default");

        return Task.FromResult(new RuntimeHealthRecord(
            Status: status,
            Context: context,
            Checks: checks,
            BlockingConditions: blockingConditions,
            Warnings: warnings,
            SuggestedRemediations: remediations));
    }

    private static bool IsGitWorktreePointer(string path)
    {
        if (!System.IO.File.Exists(path))
        {
            return false;
        }

        try
        {
            return System.IO.File.ReadLines(path).FirstOrDefault()?.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public DoctorResponse BuildDoctor(RuntimeHealthRecord health)
    {
        var findings = new List<DoctorFinding>();

        foreach (var blocking in health.BlockingConditions)
        {
            findings.Add(new DoctorFinding(
                Severity: "Error",
                Category: "Readiness",
                Summary: blocking,
                Cause: blocking,
                Remediation: FirstMatchingRemediation(health, blocking),
                RelatedCapabilityIds: ["build", "test", "patch.apply", "patch.validate"]));
        }

        foreach (var warning in health.Warnings)
        {
            findings.Add(new DoctorFinding(
                Severity: "Warning",
                Category: "Compatibility",
                Summary: warning,
                Cause: warning,
                Remediation: FirstMatchingRemediation(health, warning),
                RelatedCapabilityIds: ["capabilities", "diagnostics", "build", "test"]));
        }

        return new DoctorResponse(health, findings);
    }

    private static string FirstMatchingRemediation(RuntimeHealthRecord health, string message)
    {
        if (message.Contains("indexed", StringComparison.OrdinalIgnoreCase))
        {
            return health.SuggestedRemediations.FirstOrDefault(
                       remediation => remediation.Contains("devx index", StringComparison.OrdinalIgnoreCase))
                   ?? "Index the repository before continuing.";
        }

        if (message.Contains("target", StringComparison.OrdinalIgnoreCase))
        {
            return health.SuggestedRemediations.FirstOrDefault(
                       remediation => remediation.Contains("runtime target", StringComparison.OrdinalIgnoreCase))
                   ?? "Select a supported runtime target.";
        }

        if (message.Contains(".git", StringComparison.OrdinalIgnoreCase) || message.Contains("Git", StringComparison.OrdinalIgnoreCase))
        {
            return health.SuggestedRemediations.FirstOrDefault(
                       remediation => remediation.Contains("Git", StringComparison.OrdinalIgnoreCase))
                   ?? "Open or initialize the repository from its Git root.";
        }

        return health.SuggestedRemediations.FirstOrDefault() ?? "Review runtime readiness diagnostics and retry.";
    }
}
