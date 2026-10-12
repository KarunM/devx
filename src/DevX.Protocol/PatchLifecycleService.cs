using System.Text.RegularExpressions;
using System.Text.Json;
using DevX.Domain;
using IOFile = System.IO.File;

namespace DevX.Protocol;

public sealed class PatchLifecycleService
{
    private readonly Action<string, int>? _applyFaultHook;

    public PatchLifecycleService()
    {
    }

    // Kept internal so tests can exercise commit and rollback failures without exposing
    // a production fault-injection API.
    internal PatchLifecycleService(Action<string, int> applyFaultHook)
    {
        _applyFaultHook = applyFaultHook;
    }

    public PatchPreviewResponse Preview(string repositoryRoot, PatchDocument patch)
    {
        var validation = Validate(repositoryRoot, patch);
        var diffPreview = string.Join("\n", patch.Operations.Select(BuildPreview));
        return new PatchPreviewResponse(validation.Errors.Count == 0, validation.Summary with { LifecycleStage = "preview" }, diffPreview, validation.Errors);
    }

    public PatchValidateResponse ValidateDocument(string repositoryRoot, PatchDocument patch)
    {
        var validation = Validate(repositoryRoot, patch);
        var warnings = validation.Errors.Where(error => error.Code == "Warning").Select(error => error.Message).ToArray();
        var errors = validation.Errors.Where(error => error.Code != "Warning").Select(error => error.Message).ToArray();
        return new PatchValidateResponse(errors.Length == 0, validation.Summary with { LifecycleStage = "validate" }, warnings, errors);
    }

    public (PatchApplyResponse? Response, ProtocolError? Error) Apply(string repositoryRoot, PatchDocument patch)
    {
        var normalizedRoot = Path.GetFullPath(repositoryRoot);
        var normalizedOperations = new List<PatchOperation>(patch.Operations.Count);
        foreach (var operation in patch.Operations)
        {
            if (!TryNormalizeApplyPath(normalizedRoot, operation.Path, out var relativePath, out var pathError))
            {
                return (null, new ProtocolError("ValidationError", pathError ?? "Patch target path is invalid."));
            }

            if (ContainsReparsePoint(normalizedRoot, relativePath, out var linkError))
            {
                return (null, new ProtocolError("ValidationError", linkError ?? "Patch target path could not be checked safely."));
            }

            normalizedOperations.Add(operation with { Path = relativePath });
        }

        // Keep the existing response summary paths exactly as supplied by the caller.
        // The normalized copies are used only for private staging and commit paths.
        var validation = Validate(normalizedRoot, patch);
        if (validation.Errors.Count > 0)
        {
            var first = validation.Errors[0];
            return (null, new ProtocolError(first.Code, first.Message));
        }

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var relativePaths = normalizedOperations.Select(operation => operation.Path).Distinct(comparer).ToArray();
        var stagingRoot = Path.Combine(Path.GetTempPath(), $"devx-patch-stage-{Guid.NewGuid():N}");
        var workRoot = Path.Combine(stagingRoot, "work");
        var backupRoot = Path.Combine(stagingRoot, "before");
        var snapshots = new Dictionary<string, FileSnapshot>(comparer);
        var preserveStaging = false;
        try
        {
            Directory.CreateDirectory(stagingRoot);
            foreach (var relativePath in relativePaths)
            {
                var sourcePath = Path.Combine(normalizedRoot, relativePath);
                var stagedPath = Path.Combine(stagingRoot, relativePath);
                if (IOFile.Exists(sourcePath))
                {
                    var bytes = IOFile.ReadAllBytes(sourcePath);
                    var workingPath = Path.Combine(workRoot, relativePath);
                    var backupPath = Path.Combine(backupRoot, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(workingPath)!);
                    Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                    IOFile.WriteAllBytes(workingPath, bytes);
                    IOFile.WriteAllBytes(backupPath, bytes);
                    RestrictRecoveryFile(backupPath);
                    snapshots.Add(relativePath, CaptureSnapshot(sourcePath, bytes));
                }
                else
                {
                    snapshots.Add(relativePath, new FileSnapshot(false, [], FileAttributes.Normal, null));
                }
            }

            // Apply in document order against staged copies. A later operation can thus
            // observe an earlier operation's result without touching live source files.
            foreach (var operation in normalizedOperations)
            {
                var result = ApplyOperation(workRoot, operation);
                if (!result.Success)
                {
                    return (null, new ProtocolError(result.Code, result.Message));
                }
            }

            foreach (var relativePath in relativePaths)
            {
                if (!TryNormalizeApplyPath(normalizedRoot, relativePath, out _, out var pathError))
                {
                    return (null, new ProtocolError("PatchCommitFailedRestored", pathError ?? "Target path could not be resolved safely."));
                }
                if (ContainsReparsePoint(normalizedRoot, relativePath, out var linkError))
                {
                    return (null, new ProtocolError("PatchCommitFailedRestored", linkError ?? "Target path could not be checked safely."));
                }

                var livePath = Path.Combine(normalizedRoot, relativePath);
                if (!SnapshotStillMatches(livePath, snapshots[relativePath]))
                {
                    return (null, new ProtocolError("PatchCommitFailedRestored", $"Target '{relativePath}' changed while the patch was being prepared; no patch files were committed."));
                }
            }

            var createdDirectories = new List<string>();
            var attempted = new List<string>();
            try
            {
                var commitIndex = 0;
                foreach (var relativePath in relativePaths)
                {
                    _applyFaultHook?.Invoke("commit", commitIndex++);
                    attempted.Add(relativePath);
                    var livePath = Path.Combine(normalizedRoot, relativePath);
                    var stagedPath = Path.Combine(workRoot, relativePath);
                    if (IOFile.Exists(stagedPath))
                    {
                        EnsureParentDirectories(Path.GetDirectoryName(livePath)!, normalizedRoot, createdDirectories);
                        CommitStagedFile(stagedPath, livePath, snapshots[relativePath]);
                    }
                    else if (IOFile.Exists(livePath))
                    {
                        IOFile.Delete(livePath);
                    }
                }
            }
            catch (Exception commitException)
            {
                var restored = true;
                var restoreIndex = 0;
                var restoreFailures = new List<string>();
                foreach (var relativePath in attempted.AsEnumerable().Reverse())
                {
                    try
                    {
                        _applyFaultHook?.Invoke("restore", restoreIndex++);
                        RestoreSnapshot(Path.Combine(normalizedRoot, relativePath), snapshots[relativePath]);
                    }
                    catch (Exception restoreException)
                    {
                        restored = false;
                        restoreFailures.Add($"{relativePath}: {restoreException.Message}");
                    }
                }

                if (restored)
                {
                    RemoveCreatedDirectories(createdDirectories);
                }

                if (restored)
                {
                    return (null, new ProtocolError("PatchCommitFailedRestored", $"Patch commit failed and all affected files were restored: {commitException.Message}"));
                }

                preserveStaging = true;
                var recovery = TryWriteRecoveryBundle(stagingRoot, "uncertain-commit", snapshots, relativePaths, attempted, restoreFailures, commitException.Message, null);
                return (null, new ProtocolError("PatchCommitOutcomeUncertain", $"Patch commit failed and rollback was incomplete; repository state may have changed. Recovery data is at '{recovery.Location}'. {recovery.Error ?? commitException.Message}"));
            }
        }
        catch (Exception ex)
        {
            // All work before the live commit is confined to the temporary stage tree.
            return (null, new ProtocolError("PatchCommitFailedRestored", $"Patch could not be prepared; no repository files were changed: {ex.Message}"));
        }
        finally
        {
            if (!preserveStaging)
            {
                try { Directory.Delete(stagingRoot, recursive: true); }
                catch { /* best-effort cleanup of private staging data */ }
            }
        }

        return (new PatchApplyResponse(true, validation.Summary with { LifecycleStage = "apply" }, validation.Errors), null);
    }

    private static bool TryNormalizeApplyPath(string root, string inputPath, out string relativePath, out string? error)
    {
        relativePath = string.Empty;
        if (!RepositoryPathGuard.TryResolve(root, inputPath, out var absolutePath, out var pathError))
        {
            error = pathError;
            return false;
        }
        error = null;
        relativePath = Path.GetRelativePath(Path.GetFullPath(root), absolutePath);
        return true;
    }

    private static bool ContainsReparsePoint(string root, string relativePath, out string? error)
    {
        error = null;
        var current = Path.GetFullPath(root);
        var components = relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        foreach (var component in components)
        {
            current = Path.Combine(current, component);
            try
            {
                var attributes = IOFile.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    error = $"Patch path '{relativePath}' contains a symbolic link or reparse point and cannot be staged safely.";
                    return true;
                }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = $"Patch path '{relativePath}' could not be checked safely: {ex.Message}";
                return true;
            }
        }

        return false;
    }

    private static FileSnapshot CaptureSnapshot(string path, byte[] bytes)
    {
        var attributes = IOFile.GetAttributes(path);
        UnixFileMode? unixMode = null;
        if (!OperatingSystem.IsWindows())
        {
            try { unixMode = IOFile.GetUnixFileMode(path); }
            catch (PlatformNotSupportedException) { }
            catch (IOException) { }
        }

        return new FileSnapshot(true, bytes, attributes, unixMode);
    }

    private static bool SnapshotStillMatches(string path, FileSnapshot snapshot)
    {
        if (!IOFile.Exists(path)) return !snapshot.Exists;
        if (!snapshot.Exists) return false;
        return IOFile.ReadAllBytes(path).AsSpan().SequenceEqual(snapshot.Bytes);
    }

    private static void EnsureParentDirectories(string parent, string root, List<string> createdDirectories)
    {
        var missing = new Stack<string>();
        var current = parent;
        while (!Directory.Exists(current))
        {
            var full = Path.GetFullPath(current);
            var relative = Path.GetRelativePath(root, full);
            if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
                throw new IOException("Patch target parent is outside the repository root.");
            missing.Push(full);
            current = Path.GetDirectoryName(full) ?? throw new IOException("Patch target parent could not be resolved.");
        }

        while (missing.Count > 0)
        {
            var directory = missing.Pop();
            Directory.CreateDirectory(directory);
            createdDirectories.Add(directory);
        }
    }

    private static void CommitStagedFile(string stagedPath, string targetPath, FileSnapshot snapshot)
    {
        var parent = Path.GetDirectoryName(targetPath)!;
        var tempPath = Path.Combine(parent, $".devx-patch-{Guid.NewGuid():N}.tmp");
        try
        {
            IOFile.Copy(stagedPath, tempPath, overwrite: false);
            if (snapshot.Exists)
            {
                if (snapshot.UnixMode is UnixFileMode mode && !OperatingSystem.IsWindows())
                    IOFile.SetUnixFileMode(tempPath, mode);
                IOFile.SetAttributes(tempPath, snapshot.Attributes & ~FileAttributes.ReparsePoint);
                if ((snapshot.Attributes & FileAttributes.ReadOnly) != 0 && IOFile.Exists(targetPath))
                    IOFile.SetAttributes(targetPath, snapshot.Attributes & ~FileAttributes.ReadOnly);
            }

            IOFile.Move(tempPath, targetPath, overwrite: true);
        }
        finally
        {
            if (IOFile.Exists(tempPath)) IOFile.Delete(tempPath);
        }
    }

    private static void RestoreSnapshot(string path, FileSnapshot snapshot)
    {
        if (!snapshot.Exists)
        {
            if (IOFile.Exists(path)) IOFile.Delete(path);
            return;
        }

        var parent = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(parent);
        var tempPath = Path.Combine(parent, $".devx-restore-{Guid.NewGuid():N}.tmp");
        try
        {
            IOFile.WriteAllBytes(tempPath, snapshot.Bytes);
            if (snapshot.UnixMode is UnixFileMode mode && !OperatingSystem.IsWindows())
                IOFile.SetUnixFileMode(tempPath, mode);
            if (IOFile.Exists(path) && (IOFile.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                IOFile.SetAttributes(path, IOFile.GetAttributes(path) & ~FileAttributes.ReadOnly);
            IOFile.Move(tempPath, path, overwrite: true);
            IOFile.SetAttributes(path, snapshot.Attributes & ~FileAttributes.ReparsePoint);
        }
        finally
        {
            if (IOFile.Exists(tempPath)) IOFile.Delete(tempPath);
        }
    }

    private static void RemoveCreatedDirectories(IEnumerable<string> directories)
    {
        foreach (var directory in directories.Reverse())
        {
            try { if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory); }
            catch { }
        }
    }

    private static RecoveryBundleResult TryWriteRecoveryBundle(
        string recoveryRoot,
        string outcome,
        IReadOnlyDictionary<string, FileSnapshot> snapshots,
        IReadOnlyList<string> affectedPaths,
        IReadOnlyList<string> attemptedPaths,
        IReadOnlyList<string> failures,
        string failure,
        IReadOnlyDictionary<string, string?>? requestedBeforeStates,
        IReadOnlyDictionary<string, string>? pathKinds = null)
    {
        try
        {
            Directory.CreateDirectory(recoveryRoot);
            RestrictRecoveryDirectory(recoveryRoot);
            var beforeRoot = Path.Combine(recoveryRoot, "before");
            Directory.CreateDirectory(beforeRoot);
            RestrictRecoveryDirectory(beforeRoot);
            var files = new List<object>();
            foreach (var path in affectedPaths)
            {
                snapshots.TryGetValue(path, out var snapshot);
                var kind = pathKinds is not null && pathKinds.TryGetValue(path, out var suppliedKind)
                    ? suppliedKind
                    : snapshot?.Exists == true ? "file" : "missing";
                string? backup = null;
                if (snapshot?.Exists == true)
                {
                    // Numbered filenames keep even malformed or formerly absolute paths
                    // from escaping the private recovery directory.
                    var backupPath = Path.Combine(beforeRoot, $"{files.Count:D4}.bin");
                    Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                    IOFile.WriteAllBytes(backupPath, snapshot.Bytes);
                    RestrictRecoveryFile(backupPath);
                    backup = Path.GetRelativePath(recoveryRoot, backupPath);
                }

                var requestedBefore = requestedBeforeStates is not null && requestedBeforeStates.TryGetValue(path, out var requested)
                    ? requested
                    : null;
                files.Add(new
                {
                    path,
                    kind,
                    backup,
                    attributes = snapshot?.Attributes.ToString(),
                    unixMode = snapshot?.UnixMode?.ToString(),
                    requestedBeforeState = requestedBefore
                });
            }

            var manifest = new
            {
                format = "devx-patch-recovery-v1",
                createdUtc = DateTimeOffset.UtcNow,
                outcome,
                failure,
                affectedPaths,
                attemptedPaths,
                failures,
                files
            };
            IOFile.WriteAllText(Path.Combine(recoveryRoot, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            RestrictRecoveryFile(Path.Combine(recoveryRoot, "manifest.json"));
            return new RecoveryBundleResult(recoveryRoot, null);
        }
        catch (Exception ex)
        {
            // Keep the directory and any backups already written. The caller still reports
            // this location so a failed manifest write does not silently discard evidence.
            return new RecoveryBundleResult(recoveryRoot, $"Recovery manifest could not be completed: {ex.Message}");
        }
    }

    private static void RestrictRecoveryDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            try { IOFile.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            catch (PlatformNotSupportedException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void RestrictRecoveryFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            try { IOFile.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch (PlatformNotSupportedException) { }
            catch (IOException) { }
        }
    }

    private sealed record FileSnapshot(bool Exists, byte[] Bytes, FileAttributes Attributes, UnixFileMode? UnixMode);
    private sealed record RecoveryBundleResult(string Location, string? Error);

    public (PatchRollbackResponse? Response, ProtocolError? Error) Rollback(string repositoryRoot, PatchDocument patch)
    {
        var rollbackTargets = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var operation in patch.Operations)
        {
            if (!RepositoryPathGuard.TryResolve(repositoryRoot, operation.Path, out var target, out var pathError))
                return (null, new ProtocolError("ValidationError", pathError));
            rollbackTargets[operation.Path] = target;
        }

        var validation = Validate(repositoryRoot, patch);
        var unsupported = validation.Summary.Contracts.Where(contract => !contract.RollbackSupported).ToArray();
        if (unsupported.Length > 0)
        {
            var first = unsupported[0];
            return (null, new ProtocolError("ValidationError", $"Rollback does not support operation '{first.Operation}' on '{first.Path}'. {first.RollbackReason}".Trim()));
        }

        var restoredFiles = 0;
        var revertedOperations = 0;
        var mutationAttempted = false;
        var rollbackSnapshots = new Dictionary<string, FileSnapshot>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var rollbackPathKinds = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var rollbackPaths = patch.Operations.Select(operation => operation.Path).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
        foreach (var path in rollbackPaths)
        {
            var absolutePath = rollbackTargets[path];
            if (IOFile.Exists(absolutePath))
            {
                var bytes = IOFile.ReadAllBytes(absolutePath);
                rollbackSnapshots[path] = CaptureSnapshot(absolutePath, bytes);
                rollbackPathKinds[path] = "file";
            }
            else if (Directory.Exists(absolutePath))
            {
                rollbackSnapshots[path] = new FileSnapshot(false, [], FileAttributes.Directory, null);
                rollbackPathKinds[path] = "directory";
            }
            else
            {
                rollbackSnapshots[path] = new FileSnapshot(false, [], FileAttributes.Normal, null);
                rollbackPathKinds[path] = "missing";
            }
        }
        var rollbackAttempted = new List<string>();

        ProtocolError Failure(string code, string message)
        {
            if (!mutationAttempted) return new ProtocolError(code, message);
            var beforeStates = new Dictionary<string, string?>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var operation in patch.Operations) beforeStates[operation.Path] = operation.BeforeState;
            var recovery = TryWriteRecoveryBundle(
                Path.Combine(Path.GetTempPath(), $"devx-patch-stage-{Guid.NewGuid():N}"),
                "uncertain-rollback", rollbackSnapshots, rollbackPaths, rollbackAttempted, [message], message, beforeStates, rollbackPathKinds);
            return new ProtocolError("PatchRollbackOutcomeUncertain", $"Rollback stopped after source mutation may have begun; repository state may have changed. Recovery data is at '{recovery.Location}'. {recovery.Error ?? message}");
        }

        try
        {
            foreach (var operation in patch.Operations.Reverse())
            {
                var absolutePath = rollbackTargets[operation.Path];

                if (operation.Operation.Equals("CreateFile", StringComparison.OrdinalIgnoreCase))
                {
                    if (!IOFile.Exists(absolutePath))
                    {
                        return (null, Failure("NotFound", $"File '{operation.Path}' was not found for rollback."));
                    }

                    mutationAttempted = true;
                    rollbackAttempted.Add(operation.Path);
                    IOFile.Delete(absolutePath);
                    restoredFiles++;
                    revertedOperations++;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(operation.BeforeState))
                {
                    return (null, Failure("ValidationError", $"Rollback requires before-state metadata for operation '{operation.Operation}'."));
                }

                mutationAttempted = true;
                rollbackAttempted.Add(operation.Path);
                IOFile.WriteAllText(absolutePath, operation.BeforeState);
                restoredFiles++;
                revertedOperations++;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (null, Failure("RollbackFailed", exception.Message));
        }

        return (new PatchRollbackResponse(true, validation.Summary with { LifecycleStage = "rollback" }, restoredFiles, revertedOperations, [], []), null);
    }

    private static PatchValidationResult Validate(string repositoryRoot, PatchDocument patch)
    {
        var normalizedRoot = Path.GetFullPath(repositoryRoot);
        var errors = new List<ProtocolDiagnostic>();
        var seenOperations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var contracts = new List<PatchOperationContract>();

        foreach (var operation in patch.Operations)
        {
            var absolutePath = Path.GetFullPath(Path.Combine(normalizedRoot, operation.Path));
            var rollback = GetRollbackContract(operation);
            contracts.Add(new PatchOperationContract(operation.Operation, operation.Path, rollback.RollbackSupported, rollback.RollbackAvailability, rollback.RollbackReason));

            var operationKey = $"{operation.Operation}:{operation.Path}";
            if (!seenOperations.Add(operationKey))
            {
                errors.Add(new ProtocolDiagnostic("ValidationError", $"Duplicate operation detected for '{operation.Operation}' on '{operation.Path}'."));
            }

            if (!absolutePath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new ProtocolDiagnostic("ValidationError", $"Path '{operation.Path}' is outside the repository root."));
                continue;
            }

            if (IsUnsafeUpdateFile(operation, absolutePath))
            {
                errors.Add(new ProtocolDiagnostic("ValidationError", "Whole-file replacement is not permitted for source files."));
            }

            if (operation.Operation.Equals("CreateFile", StringComparison.OrdinalIgnoreCase) && IOFile.Exists(absolutePath))
            {
                errors.Add(new ProtocolDiagnostic("ValidationError", $"Target file '{operation.Path}' already exists."));
            }

            if (!operation.Operation.Equals("CreateFile", StringComparison.OrdinalIgnoreCase) && !IOFile.Exists(absolutePath))
            {
                errors.Add(new ProtocolDiagnostic("ValidationError", $"Target file '{operation.Path}' does not exist."));
            }

            if (IsTextReplace(operation.Operation) && string.IsNullOrEmpty(operation.OldText))
                errors.Add(new ProtocolDiagnostic("ValidationError", "ReplaceText requires oldText."));
            if (IsTextReplace(operation.Operation) && operation.ReplacementText is null)
                errors.Add(new ProtocolDiagnostic("ValidationError", "ReplaceText requires replacementText."));
            if (IsTextReplace(operation.Operation) && IOFile.Exists(absolutePath) && !string.IsNullOrEmpty(operation.OldText))
            {
                var matches = CountOccurrences(IOFile.ReadAllText(absolutePath), operation.OldText);
                if (matches == 0) errors.Add(new ProtocolDiagnostic("ValidationError", $"ReplaceText old text was not found in [{operation.Path}] (0 matches)."));
                else if (!operation.ReplaceAll && operation.Occurrence is null && matches > 1) errors.Add(new ProtocolDiagnostic("ValidationError", $"ReplaceText is ambiguous in [{operation.Path}] ({matches} matches); specify occurrence or ReplaceAll."));
                else if (operation.Occurrence is int occurrence && (occurrence < 1 || occurrence > matches)) errors.Add(new ProtocolDiagnostic("ValidationError", $"ReplaceText occurrence {occurrence} is out of range ({matches} matches)."));
            }

            if (operation.Operation.Equals("ReplaceMethod", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(operation.ReplacementText))
            {
                errors.Add(new ProtocolDiagnostic("ValidationError", "ReplaceMethod requires replacementText."));
            }

            if (operation.Operation.Equals("ReplaceMethod", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(operation.MethodName) && IOFile.Exists(absolutePath))
            {
                var replaceMethodMatches = StructuredSymbolReader.CanParse(absolutePath)
                    ? FindStructuredMethods(absolutePath, operation.MethodName).Count
                    : Regex.Matches(IOFile.ReadAllText(absolutePath), $@"(?m)^[ \t]*(?:public|private|protected|internal|static|async|sealed|override|partial|virtual|extern|unsafe|new|readonly|ref)[^\n]*\b{Regex.Escape(operation.MethodName)}\b[^\n]*$").Count;
                if (replaceMethodMatches == 0)
                {
                    errors.Add(new ProtocolDiagnostic("ValidationError", $"ReplaceMethod target [{operation.MethodName}] was not found in [{operation.Path}]."));
                }
                else if (replaceMethodMatches > 1)
                {
                    errors.Add(new ProtocolDiagnostic("ValidationError", $"ReplaceMethod target [{operation.MethodName}] is ambiguous in [{operation.Path}] ({replaceMethodMatches} matches)."));
                }
            }

            if (operation.Operation.Equals("InsertMethod", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(operation.MethodText))
            {
                errors.Add(new ProtocolDiagnostic("ValidationError", "InsertMethod requires methodText."));
            }

            if (operation.Operation.Equals("AddUsing", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(operation.Namespace))
            {
                errors.Add(new ProtocolDiagnostic("ValidationError", "AddUsing requires namespace."));
            }

            if (operation.Operation.Equals("AddUsing", StringComparison.OrdinalIgnoreCase) && StructuredSymbolReader.CanParse(absolutePath))
            {
                errors.Add(new ProtocolDiagnostic("UnsupportedLanguage", "AddUsing is C#-only; use an explicit import/text patch for Python or Markdown."));
            }

            if (operation.Operation.Equals("UpdateFile", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new ProtocolDiagnostic("ValidationError", "UpdateFile remains blocked for source files."));
            }
        }

        var filesAffected = patch.Operations.Select(operation => operation.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var operationClasses = patch.Operations.Select(operation => operation.Operation).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var rollbackSupported = contracts.All(contract => contract.RollbackSupported);
        var summary = new PatchLifecycleSummary(
            "validate",
            "patch",
            patch.Operations.Count,
            filesAffected.Length,
            filesAffected,
            operationClasses,
            rollbackSupported,
            contracts);

        return new PatchValidationResult(summary, errors);
    }

    private static PatchOperationRollbackContract GetRollbackContract(PatchOperation operation)
    {
        if (operation.Operation.Equals("CreateFile", StringComparison.OrdinalIgnoreCase))
        {
            return new PatchOperationRollbackContract(true, "Supported", null);
        }

        if (IsTextReplace(operation.Operation) || operation.Operation.Equals("ReplaceMethod", StringComparison.OrdinalIgnoreCase)
            || operation.Operation.Equals("InsertMethod", StringComparison.OrdinalIgnoreCase)
            || operation.Operation.Equals("ReplaceLineRange", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(operation.BeforeState)
                ? new PatchOperationRollbackContract(false, "Unavailable", "Before-state metadata is required.")
                : new PatchOperationRollbackContract(true, "Supported", null);
        }

        return operation.Operation.Equals("DeleteFile", StringComparison.OrdinalIgnoreCase)
            ? new PatchOperationRollbackContract(false, "Unavailable", "DeleteFile rollback requires backup content.")
            : new PatchOperationRollbackContract(false, "Unavailable", $"Rollback is not implemented for operation '{operation.Operation}'.");
    }

    private static (bool Success, string Code, string Message) ApplyOperation(string root, PatchOperation operation)
    {
        var normalizedRoot = Path.GetFullPath(root);
        var targetPath = Path.GetFullPath(Path.Combine(normalizedRoot, operation.Path));
        if (!targetPath.StartsWith(normalizedRoot, StringComparison.Ordinal))
        {
            return (false, "ValidationError", "Patch targets must remain inside the repository root.");
        }

        if (IsUnsafeUpdateFile(operation, targetPath))
        {
            return (false, "UnsafeOperation", "Whole-file replacement is not permitted for source files.");
        }

        return operation.Operation switch
        {
            "CreateFile" => ApplyCreateFile(targetPath, operation.Content),
            "DeleteFile" => ApplyDeleteFile(targetPath),
            "ReplaceMethod" => ApplyReplaceMethod(targetPath, operation.MethodName, operation.ReplacementText),
            "ReplaceText" or "TextReplace" => ApplyTextReplace(targetPath, operation.OldText, operation.ReplacementText, operation.Occurrence, operation.ReplaceAll),
            "InsertMethod" => ApplyInsertMethod(targetPath, operation.ContainingType, operation.TypeId, operation.MethodText, operation.Position),
            "AddUsing" => ApplyAddUsing(targetPath, operation.Namespace),
            "InsertText" => ApplyInsertText(targetPath, operation.Content, operation.Position),
            "ReplaceLineRange" => ApplyReplaceLineRange(targetPath, operation.Content, operation.Position),
            _ => (false, "InvalidOperation", $"Unsupported patch operation '{operation.Operation}'.")
        };
    }

    private static bool IsTextReplace(string operation) => operation.Equals("ReplaceText", StringComparison.OrdinalIgnoreCase) || operation.Equals("TextReplace", StringComparison.OrdinalIgnoreCase);
    private static int CountOccurrences(string text, string value)
    {
        var count = 0; var offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0) { count++; offset += value.Length; }
        return count;
    }
    private static (bool Success, string Code, string Message) ApplyTextReplace(string targetPath, string? oldText, string? replacement, int? occurrence, bool replaceAll)
    {
        if (!IOFile.Exists(targetPath)) return (false, "NotFound", "Target file does not exist.");
        if (string.IsNullOrEmpty(oldText) || replacement is null) return (false, "ValidationError", "ReplaceText requires oldText and replacementText.");
        var text = IOFile.ReadAllText(targetPath); var count = CountOccurrences(text, oldText);
        if (count == 0) return (false, "ValidationError", "ReplaceText old text was not found (0 matches).");
        if (!replaceAll && occurrence is null && count > 1) return (false, "ValidationError", $"ReplaceText is ambiguous ({count} matches); specify occurrence or ReplaceAll.");
        if (occurrence is int index)
        {
            if (index < 1 || index > count) return (false, "ValidationError", $"ReplaceText occurrence {index} is out of range ({count} matches).");
            var start = -1; var from = 0;
            for (var i = 0; i < index; i++) { start = text.IndexOf(oldText, from, StringComparison.Ordinal); from = start + oldText.Length; }
            text = text[..start] + replacement + text[(start + oldText.Length)..];
        }
        else text = replaceAll ? text.Replace(oldText, replacement, StringComparison.Ordinal) : text.Replace(oldText, replacement, StringComparison.Ordinal);
        IOFile.WriteAllText(targetPath, text); return (true, string.Empty, string.Empty);
    }

    private static (bool Success, string Code, string Message) ApplyCreateFile(string targetPath, string? content)
    {
        if (IOFile.Exists(targetPath))
        {
            return (false, "ValidationError", "Target file already exists.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        IOFile.WriteAllText(targetPath, content ?? string.Empty);
        return (true, string.Empty, string.Empty);
    }

    private static (bool Success, string Code, string Message) ApplyDeleteFile(string targetPath)
    {
        if (!IOFile.Exists(targetPath))
        {
            return (false, "NotFound", "Target file does not exist.");
        }

        IOFile.Delete(targetPath);
        return (true, string.Empty, string.Empty);
    }

    private static (bool Success, string Code, string Message) ApplyReplaceMethod(string targetPath, string? methodName, string? replacementText)
    {
        if (!IOFile.Exists(targetPath))
        {
            return (false, "NotFound", "Target file does not exist.");
        }

        if (string.IsNullOrWhiteSpace(methodName) || string.IsNullOrWhiteSpace(replacementText))
        {
            return (false, "ValidationError", "ReplaceMethod requires methodName and replacementText.");
        }

        var text = IOFile.ReadAllText(targetPath);
        if (StructuredSymbolReader.CanParse(targetPath))
        {
            var methods = FindStructuredMethods(targetPath, methodName);
            if (methods.Count != 1)
            {
                return (false, "ValidationError", methods.Count == 0 ? "Target method was not found." : "Target method must be unambiguous.");
            }

            IOFile.WriteAllText(targetPath, ReplaceLineSpan(text, methods[0].StartLine, methods[0].EndLine, replacementText));
            return (true, string.Empty, string.Empty);
        }

        var methodPattern = $@"(?m)^[ \t]*(?:public|private|protected|internal|static|async|sealed|override|partial|virtual|extern|unsafe|new|readonly|ref|internal\s+static|private\s+static|public\s+static|protected\s+static)[^\n]*\b{Regex.Escape(methodName)}\b[^\n]*$";
        var signatureMatches = Regex.Matches(text, methodPattern);
        if (signatureMatches.Count != 1)
        {
            return (false, "ValidationError", "Target method must be unambiguous.");
        }

        var signatureMatch = signatureMatches[0];
        var bodyStart = text.IndexOf('{', signatureMatch.Index + signatureMatch.Length);
        if (bodyStart < 0)
        {
            return (false, "ValidationError", "Target method body was not found.");
        }

        var bodyEnd = FindMatchingBrace(text, bodyStart);
        if (bodyEnd < 0)
        {
            return (false, "ValidationError", "Target method body was not balanced.");
        }

        var matchStart = signatureMatch.Index;
        var matchLength = bodyEnd + 1 - matchStart;
        var updated = text[..matchStart] + replacementText + text[(matchStart + matchLength)..];
        IOFile.WriteAllText(targetPath, updated);
        return (true, string.Empty, string.Empty);
    }

    private static int FindMatchingBrace(string text, int openBraceIndex)
    {
        var depth = 0;
        for (var index = openBraceIndex; index < text.Length; index++)
        {
            if (text[index] == '{')
            {
                depth++;
            }
            else if (text[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return index;
                }
            }
        }

        return -1;
    }

    private static (bool Success, string Code, string Message) ApplyInsertMethod(string targetPath, string? containingType, string? typeId, string? methodText, string? position)
    {
        if (!IOFile.Exists(targetPath))
        {
            return (false, "NotFound", "Target file does not exist.");
        }

        if (string.IsNullOrWhiteSpace(methodText) || string.IsNullOrWhiteSpace(position))
        {
            return (false, "ValidationError", "InsertMethod requires containingType or typeId, methodText, and position.");
        }

        var text = IOFile.ReadAllText(targetPath);
        if (MarkdownSymbolReader.CanParse(targetPath))
        {
            // Markdown has no indentation contract the way Python does: a section is inserted
            // verbatim on its own lines, anchored on a sibling heading's span.
            return InsertMarkdownSection(targetPath, text, containingType, methodText!, position!);
        }

        if (StructuredSymbolReader.CanParse(targetPath))
        {
            var parsed = StructuredSymbolReader.Parse(targetPath);
            var type = parsed.Types.FirstOrDefault(item => string.IsNullOrWhiteSpace(containingType)
                || string.Equals(item.Name, containingType, StringComparison.Ordinal));
            if (type is null)
            {
                return (false, "ValidationError", "Target type or section must exist and be unambiguous.");
            }

            var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
            var anchorName = position[(position.IndexOf(':') + 1)..];
            var pyAnchor = type.Methods.FirstOrDefault(item => string.Equals(item.Name, anchorName, StringComparison.Ordinal));
            var isClass = !string.Equals(type.Name, "Module", StringComparison.Ordinal);
            var indent = isClass && pyAnchor is not null
                ? new string(lines[pyAnchor.StartLine - 1].TakeWhile(char.IsWhiteSpace).ToArray())
                : isClass ? "    " : string.Empty;
            var inserted = NormalizePythonIndent(methodText, indent).Split('\n');
            var pyInsertAt = pyAnchor is null
                ? (isClass ? type.EndLine - 1 : lines.Count)
                : position.StartsWith("Before:", StringComparison.OrdinalIgnoreCase) ? pyAnchor.StartLine - 1 : pyAnchor.EndLine;
            lines.InsertRange(Math.Clamp(pyInsertAt, 0, lines.Count), inserted);
            IOFile.WriteAllText(targetPath, string.Join("\n", lines));
            return (true, string.Empty, string.Empty);
        }

        var typeMatch = !string.IsNullOrWhiteSpace(containingType)
            ? Regex.Match(text, $@"class\s+{Regex.Escape(containingType)}[\s\S]*?\{{")
            : typeId is null ? Regex.Match(text, @"class\s+[A-Za-z_][A-Za-z0-9_]*[\s\S]*?\{") : Regex.Match(text, @"class\s+[A-Za-z_][A-Za-z0-9_]*[\s\S]*?\{");

        if (!typeMatch.Success)
        {
            return (false, "ValidationError", "Target type must exist and be unambiguous.");
        }

        var anchor = position[(position.IndexOf(':') + 1)..];
        var insertAt = position.StartsWith("Before:", StringComparison.OrdinalIgnoreCase) || position.StartsWith("After:", StringComparison.OrdinalIgnoreCase)
            ? text.IndexOf(anchor, StringComparison.Ordinal)
            : text.LastIndexOf('}');

        if (insertAt < 0)
        {
            return (false, "ValidationError", "Insertion point was not found.");
        }

        var updated = position.StartsWith("Before:", StringComparison.OrdinalIgnoreCase)
            ? text[..insertAt] + methodText + Environment.NewLine + text[insertAt..]
            : position.StartsWith("After:", StringComparison.OrdinalIgnoreCase)
                ? text[..(insertAt + anchor.Length)] + Environment.NewLine + methodText + text[(insertAt + anchor.Length)..]
                : text[..insertAt] + Environment.NewLine + methodText + Environment.NewLine + text[insertAt..];

        IOFile.WriteAllText(targetPath, updated);
        return (true, string.Empty, string.Empty);
    }

    /// <summary>
    /// Inserts a Markdown section relative to an existing heading. `position` is
    /// Before:&lt;heading&gt; / After:&lt;heading&gt;; with no resolvable anchor the section is appended to
    /// the end of the containing section, or the end of the document when none is named.
    /// </summary>
    private static (bool Ok, string Code, string Message) InsertMarkdownSection(
        string targetPath,
        string text,
        string? containingType,
        string sectionText,
        string position)
    {
        var parsed = MarkdownSymbolReader.Parse(targetPath, text);
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();

        var section = string.IsNullOrWhiteSpace(containingType)
            ? null
            : parsed.Types.FirstOrDefault(item => string.Equals(item.Name, containingType, StringComparison.Ordinal));

        if (!string.IsNullOrWhiteSpace(containingType) && section is null)
        {
            return (false, "ValidationError", "Target type or section must exist and be unambiguous.");
        }

        var separatorIndex = position.IndexOf(':');
        var anchorName = separatorIndex >= 0 ? position[(separatorIndex + 1)..].Trim() : string.Empty;
        var before = position.StartsWith("Before:", StringComparison.OrdinalIgnoreCase);

        var anchor = string.IsNullOrWhiteSpace(anchorName)
            ? null
            : (section?.Methods ?? parsed.Types.SelectMany(type => type.Methods).ToArray())
                .FirstOrDefault(item => string.Equals(item.Name, anchorName, StringComparison.Ordinal))
              ?? (ParsedMethod?)null;

        var anchorType = anchor is null && !string.IsNullOrWhiteSpace(anchorName)
            ? parsed.Types.FirstOrDefault(item => string.Equals(item.Name, anchorName, StringComparison.Ordinal))
            : null;

        int insertAt;
        if (anchor is not null)
        {
            insertAt = before ? anchor.StartLine - 1 : anchor.EndLine;
        }
        else if (anchorType is not null)
        {
            insertAt = before ? anchorType.StartLine - 1 : anchorType.EndLine;
        }
        else if (section is not null)
        {
            insertAt = section.EndLine;
        }
        else
        {
            insertAt = lines.Count;
        }

        insertAt = Math.Clamp(insertAt, 0, lines.Count);

        var inserted = new List<string>();
        if (insertAt > 0 && !string.IsNullOrWhiteSpace(lines[insertAt - 1]))
        {
            inserted.Add(string.Empty);
        }

        inserted.AddRange(sectionText.Replace("\r\n", "\n").TrimEnd('\n').Split('\n'));

        if (insertAt < lines.Count && !string.IsNullOrWhiteSpace(lines[insertAt]))
        {
            inserted.Add(string.Empty);
        }

        lines.InsertRange(insertAt, inserted);
        IOFile.WriteAllText(targetPath, string.Join("\n", lines));
        return (true, string.Empty, string.Empty);
    }

    private static IReadOnlyList<ParsedMethod> FindStructuredMethods(string targetPath, string methodName)
        => StructuredSymbolReader.Parse(targetPath).Types
            .SelectMany(type => type.Methods)
            .Where(method => string.Equals(method.Name, methodName, StringComparison.Ordinal))
            .ToArray();

    private static string ReplaceLineSpan(string text, int startLine, int endLine, string replacement)
    {
        var hadTrailingNewline = text.EndsWith('\n');
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        lines.RemoveRange(startLine - 1, endLine - startLine + 1);
        lines.InsertRange(startLine - 1, replacement.Replace("\r\n", "\n").Split('\n'));
        var updated = string.Join("\n", lines);
        return hadTrailingNewline && !updated.EndsWith('\n') ? updated + "\n" : updated;
    }

    private static string NormalizePythonIndent(string methodText, string indent)
    {
        var lines = methodText.Replace("\r\n", "\n").Split('\n');
        var nonEmpty = lines.Where(line => line.Trim().Length > 0).ToArray();
        var baseIndent = nonEmpty.Length == 0 ? 0 : nonEmpty.Min(line => line.TakeWhile(char.IsWhiteSpace).Count());
        return string.Join('\n', lines.Select(line => line.Trim().Length == 0 ? string.Empty : indent + line[Math.Min(baseIndent, line.Length)..]));
    }

    private static (bool Success, string Code, string Message) ApplyAddUsing(string targetPath, string? @namespace)
    {
        if (!IOFile.Exists(targetPath))
        {
            return (false, "NotFound", "Target file does not exist.");
        }

        if (string.IsNullOrWhiteSpace(@namespace))
        {
            return (false, "ValidationError", "AddUsing requires namespace.");
        }

        var text = IOFile.ReadAllText(targetPath);
        var usingLine = $"using {@namespace};";
        if (text.Contains(usingLine, StringComparison.Ordinal))
        {
            return (false, "ValidationError", "Duplicate using is not allowed.");
        }

        var insertAt = text.StartsWith("using ", StringComparison.Ordinal) ? text.LastIndexOf("using ", StringComparison.OrdinalIgnoreCase) : 0;
        var updated = insertAt <= 0
            ? usingLine + Environment.NewLine + text
            : text.Insert(insertAt, usingLine + Environment.NewLine);

        IOFile.WriteAllText(targetPath, updated);
        return (true, string.Empty, string.Empty);
    }

    private static (bool Success, string Code, string Message) ApplyInsertText(string targetPath, string? content, string? position)
    {
        if (!IOFile.Exists(targetPath))
        {
            return (false, "NotFound", "Target file does not exist.");
        }

        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(position))
        {
            return (false, "ValidationError", "InsertText requires Content and Position (e.g., 'after:42').");
        }

        var lines = IOFile.ReadAllLines(targetPath);
        var match = Regex.Match(position, @"(after|before):(\d+)", RegexOptions.IgnoreCase);
        if (!match.Success || !int.TryParse(match.Groups[2].Value, out var lineNumber) || lineNumber < 1 || lineNumber > lines.Length)
        {
            return (false, "ValidationError", "Position must be 'after:N' or 'before:N' where N is a valid line number.");
        }

        var insertIndex = match.Groups[1].Value.Equals("after", StringComparison.OrdinalIgnoreCase) ? lineNumber : lineNumber - 1;
        var contentLines = content.Split('\n');
        var newLines = lines.Take(insertIndex).Concat(contentLines).Concat(lines.Skip(insertIndex)).ToArray();
        IOFile.WriteAllLines(targetPath, newLines);
        return (true, string.Empty, string.Empty);
    }

    private static (bool Success, string Code, string Message) ApplyReplaceLineRange(string targetPath, string? content, string? position)
    {
        if (!IOFile.Exists(targetPath))
        {
            return (false, "NotFound", "Target file does not exist.");
        }

        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(position))
        {
            return (false, "ValidationError", "ReplaceLineRange requires Content and Position (e.g., '10:15').");
        }

        var rangeMatch = Regex.Match(position, @"(\d+):(\d+)");
        if (!rangeMatch.Success || !int.TryParse(rangeMatch.Groups[1].Value, out var startLine) || !int.TryParse(rangeMatch.Groups[2].Value, out var endLine))
        {
            return (false, "ValidationError", "Position must be 'start:end' line range.");
        }

        var lines = IOFile.ReadAllLines(targetPath);
        if (startLine < 1 || endLine > lines.Length || startLine > endLine)
        {
            return (false, "ValidationError", "Invalid line range.");
        }

        var contentLines = content.Split('\n');
        var newLines = lines.Take(startLine - 1).Concat(contentLines).Concat(lines.Skip(endLine)).ToArray();
        IOFile.WriteAllLines(targetPath, newLines);
        return (true, string.Empty, string.Empty);
    }

    private static string BuildPreview(PatchOperation operation)
        => operation.Operation switch
        {
            "CreateFile" => $"--- /dev/null\n+++ {operation.Path}\n@@\n+{operation.Content}",
            "DeleteFile" => $"--- {operation.Path}\n+++ /dev/null\n@@",
            "ReplaceText" or "TextReplace" => $"--- {operation.Path}\n+++ {operation.Path}\n@@\n-{operation.OldText}\n+{operation.ReplacementText}",
            "ReplaceMethod" => $"--- {operation.Path}\n+++ {operation.Path}\n@@\n-{operation.MethodName}\n+{operation.ReplacementText}",
            "InsertMethod" => $"--- {operation.Path}\n+++ {operation.Path}\n@@\n+{operation.MethodText}",
            "AddUsing" => $"--- {operation.Path}\n+++ {operation.Path}\n@@\n+using {operation.Namespace};",
            _ => $"--- {operation.Path}\n+++ {operation.Path}\n@@\n+unsupported operation {operation.Operation}"
        };

    private static bool IsUnsafeUpdateFile(PatchOperation operation, string targetPath)
        => operation.Operation.Equals("UpdateFile", StringComparison.OrdinalIgnoreCase)
            && (targetPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || targetPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                || targetPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase));

    private sealed record PatchValidationResult(PatchLifecycleSummary Summary, IReadOnlyList<ProtocolDiagnostic> Errors);
    private sealed record PatchOperationRollbackContract(bool RollbackSupported, string RollbackAvailability, string? RollbackReason);
}
