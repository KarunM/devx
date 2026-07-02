using System.Text.Json;
using Ecp.Domain;

namespace Ecp.Cli;

public sealed record EcpReport(
    ReportSessionSummary SessionSummary,
    IReadOnlyList<ReportOperation> OperationLog,
    ReportRepositoryMetrics RepositoryMetrics,
    ReportBuildTestMetrics BuildTestMetrics,
    string LlmMetrics,
    ReportEfficiencySummary EfficiencySummary);

public sealed record ReportSessionSummary(
    string SessionId,
    string RepositoryId,
    IReadOnlyList<string> WorkingSet,
    int SearchCount,
    int ExpandedFileCount,
    int ExpandedSymbolCount);

public sealed record ReportOperation(
    string Operation,
    DateTimeOffset Timestamp,
    double DurationMs,
    bool Success,
    string? SessionId);

public sealed record ReportRepositoryMetrics(
    int IndexedFiles,
    int SearchedFiles,
    int ExpandedFiles,
    int SnapshotsRequested,
    int MethodsExpanded,
    int PatchesApplied);

public sealed record ReportBuildTestMetrics(
    bool Available,
    string? Reason,
    int? BuildCount,
    int? SuccessfulBuilds,
    int? FailedBuilds,
    int? Warnings,
    int? Errors,
    int? TestRuns,
    int? Passed,
    int? Failed,
    int? Skipped);

public sealed record ReportEfficiencySummary(int TotalOperations, double TotalDurationMs, int RepairIterations);

public sealed class EcpReportComposer
{
    public async Task<EcpReport> ComposeAsync(SessionState session, IReadOnlyList<OperationMetric> metrics, IRepositoryStore repositoryStore)
    {
        var buildMetricsObserved = metrics.Where(metric => metric.Operation is "build" or "test").ToList();
        var buildTestAvailable = buildMetricsObserved.Count > 0;
        var buildCount = buildMetricsObserved.Count(metric => metric.Operation == "build");
        var testRuns = buildMetricsObserved.Count(metric => metric.Operation == "test");
        var repairIterations = buildMetricsObserved.Count(metric => !metric.Success);
        var diagnostics = session.DiagnosticsHistory
            .Select(entry => JsonSerializer.Deserialize<DiagnosticRecord>(entry))
            .Where(x => x is not null)
            .Select(x => x!)
            .ToList();

        return new EcpReport(
            new ReportSessionSummary(
                session.Id.Value.ToString(),
                session.RepositoryId.Value.ToString(),
                string.IsNullOrWhiteSpace(session.WorkingSet)
                    ? []
                    : session.WorkingSet.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                session.SearchHistory.Count,
                session.ExpandedFiles.Count,
                session.ExpandedSymbols.Count),
            metrics.Select(metric => new ReportOperation(metric.Operation, metric.Timestamp, metric.Duration.TotalMilliseconds, metric.Success, metric.SessionId)).ToList(),
            new ReportRepositoryMetrics(
                await repositoryStore.CountIndexedFilesAsync(session.RepositoryId),
                session.SearchHistory.Count,
                session.ExpandedFiles.Count,
                metrics.Count(metric => metric.Operation == "snapshot"),
                session.ExpandedSymbols.Count,
                session.PatchHistory.Count),
            buildTestAvailable
                ? new ReportBuildTestMetrics(true, null, buildCount, buildMetricsObserved.Count(metric => metric.Operation == "build" && metric.Success), buildMetricsObserved.Count(metric => metric.Operation == "build" && !metric.Success), diagnostics.Count(diagnostic => string.Equals(diagnostic.Severity, "warning", StringComparison.OrdinalIgnoreCase)), diagnostics.Count(diagnostic => string.Equals(diagnostic.Severity, "error", StringComparison.OrdinalIgnoreCase)), testRuns, 0, 0, 0)
                : new ReportBuildTestMetrics(false, "Build/Test executed outside ECP.", null, null, null, null, null, null, null, null, null),
            "unavailable",
            new ReportEfficiencySummary(metrics.Count, metrics.Sum(metric => metric.Duration.TotalMilliseconds), repairIterations));
    }
}
