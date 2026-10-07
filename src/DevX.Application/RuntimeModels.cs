namespace DevX.Application;

public sealed record RuntimeContext(
    string WorkspaceRoot,
    string RepositoryRoot,
    string? ProjectPath,
    string RuntimeFamily,
    string ConfigurationProfile);

public enum ValidationMode
{
    Build,
    Test
}

public sealed record RuntimePrerequisite(
    string Name,
    string Status,
    string Message);

public sealed record RuntimeTargetDescriptor(
    string RuntimeKind,
    string Label,
    string? ProjectPath,
    string AvailabilityStatus,
    IReadOnlyList<RuntimePrerequisite> Prerequisites);

public sealed record RuntimeCapabilityDescriptor(
    string CapabilityId,
    string Category,
    string Version,
    string Summary,
    string AvailabilityStatus,
    IReadOnlyList<RuntimePrerequisite> Prerequisites,
    IReadOnlyList<string> Interfaces,
    IReadOnlyList<string> Examples);

public sealed record RuntimeHealthCheck(
    string Name,
    string Status,
    string Message);

public sealed record RuntimeHealthRecord(
    string Status,
    RuntimeContext Context,
    IReadOnlyList<RuntimeHealthCheck> Checks,
    IReadOnlyList<string> BlockingConditions,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> SuggestedRemediations);

public sealed record DoctorFinding(
    string Severity,
    string Category,
    string Summary,
    string Cause,
    string Remediation,
    IReadOnlyList<string> RelatedCapabilityIds);

public sealed record DoctorResponse(
    RuntimeHealthRecord Health,
    IReadOnlyList<DoctorFinding> Findings);

public sealed record RuntimeDiscovery(
    string RepositoryRoot,
    IReadOnlyList<RuntimeTargetDescriptor> Targets);

public sealed record RuntimeContextResolution(
    bool Success,
    RuntimeContext? Context,
    RuntimeTargetDescriptor? SelectedTarget,
    string? TargetSelector,
    IReadOnlyList<string> Errors,
    IReadOnlyList<RuntimeTargetDescriptor> AvailableTargets);
