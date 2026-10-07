using System.Text.Json;
using DevX.Domain;

namespace DevX.Cli;

public sealed record DevXReport(
    ReportSessionSummary SessionSummary,
    IReadOnlyList<ReportOperation> OperationLog,
    ReportRepositoryMetrics RepositoryMetrics,
    ReportBuildTestMetrics BuildTestMetrics,
    ReportFallbackMetrics FallbackMetrics,
    string LlmMetrics,
    ReportEfficiencySummary EfficiencySummary, ReportingDetails? Reporting = null);

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
    string? SessionId,
    string? MetadataJson,
    string? AgentLabel);

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
    int? Skipped,
    string? LatestTestSummary = null);

public sealed record ReportFallbackEvent(
    string CapabilityId,
    string FailedCommand,
    string Reason,
    string FallbackAction,
    DateTimeOffset Timestamp);

public sealed record ReportFallbackFrequency(
    string CapabilityId,
    int Count);

public sealed record ReportFallbackMetrics(
    int TotalFallbacks,
    IReadOnlyList<ReportFallbackFrequency> FrequencyByCapability,
    IReadOnlyList<ReportFallbackEvent> Events);

public sealed record FallbackEventMetadata(
    string CapabilityId,
    string FailedCommand,
    string Reason,
    string FallbackAction);

public sealed record ReportEfficiencySummary(int TotalOperations, double TotalDurationMs, int RepairIterations);

public sealed class DevXReportComposer
{
    public async Task<DevXReport> ComposeAsync(SessionState session, IReadOnlyList<OperationMetric> metrics, IRepositoryStore repositoryStore)
    {
        var buildMetricsObserved = metrics.Where(metric => metric.Operation is "build" or "test").ToList();
        var buildTestAvailable = buildMetricsObserved.Count > 0;
        var buildCount = buildMetricsObserved.Count(metric => metric.Operation == "build");
        var testRuns = buildMetricsObserved.Count(metric => metric.Operation == "test");
        var latestTestSummary = metrics
            .Where(metric => metric.Operation == "test")
            .OrderByDescending(metric => metric.Timestamp)
            .Select(metric => ReadMetadataString(metric.MetadataJson, "Summary"))
            .FirstOrDefault(summary => !string.IsNullOrWhiteSpace(summary));
        var repairIterations = buildMetricsObserved.Count(metric => !metric.Success);
        var fallbackEvents = metrics
            .Where(metric => metric.Operation == "fallback" && !string.IsNullOrWhiteSpace(metric.MetadataJson))
            .Select(metric =>
            {
                var metadata = JsonSerializer.Deserialize<FallbackEventMetadata>(metric.MetadataJson!);
                return metadata is null
                    ? null
                    : new ReportFallbackEvent(metadata.CapabilityId, metadata.FailedCommand, metadata.Reason, metadata.FallbackAction, metric.Timestamp);
            })
            .Where(item => item is not null)
            .Select(item => item!)
            .ToList();
        var diagnostics = session.DiagnosticsHistory
            .Select(entry => JsonSerializer.Deserialize<DiagnosticRecord>(entry))
            .Where(x => x is not null)
            .Select(x => x!)
            .ToList();
        var root = await repositoryStore.GetRepositoryRootPathAsync(session.RepositoryId);
        var reportEvents = metrics.Select((m, index) => new AnalyticsEvent(session.Id.Value + ":" + index,
            session.RepositoryId.Value.ToString(), null, null, m.AgentLabel, m.SessionId, m.Operation, m.Timestamp,
            m.Success, m.Duration.TotalMilliseconds, m.MetadataJson, root)).ToList();
        var details = AnalyticsReportAggregator.Compose(new(null, reportEvents), DateTimeOffset.UtcNow,
            TimeSpan.FromDays(3650)).Reporting;

        return new DevXReport(
            new ReportSessionSummary(
                session.Id.Value.ToString(),
                session.RepositoryId.Value.ToString(),
                string.IsNullOrWhiteSpace(session.WorkingSet)
                    ? []
                    : session.WorkingSet.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                session.SearchHistory.Count,
                session.ExpandedFiles.Count,
                session.ExpandedSymbols.Count),
            metrics.Select(metric => new ReportOperation(metric.Operation, metric.Timestamp, metric.Duration.TotalMilliseconds, metric.Success, metric.SessionId, metric.MetadataJson, metric.AgentLabel)).ToList(),
            new ReportRepositoryMetrics(
                await repositoryStore.CountIndexedFilesAsync(session.RepositoryId),
                session.SearchHistory.Count,
                session.ExpandedFiles.Count,
                metrics.Count(metric => metric.Operation == "snapshot"),
                session.ExpandedSymbols.Count,
                session.PatchHistory.Count),
            buildTestAvailable
                ? new ReportBuildTestMetrics(true, null, buildCount, buildMetricsObserved.Count(metric => metric.Operation == "build" && metric.Success), buildMetricsObserved.Count(metric => metric.Operation == "build" && !metric.Success), diagnostics.Count(diagnostic => string.Equals(diagnostic.Severity, "warning", StringComparison.OrdinalIgnoreCase)), diagnostics.Count(diagnostic => string.Equals(diagnostic.Severity, "error", StringComparison.OrdinalIgnoreCase)), testRuns, metrics.Count(metric => metric.Operation == "test" && metric.Success), metrics.Count(metric => metric.Operation == "test" && !metric.Success), 0, latestTestSummary)
                : new ReportBuildTestMetrics(false, "Build/Test executed outside DevX.", null, null, null, null, null, null, null, null, null),
            new ReportFallbackMetrics(
                fallbackEvents.Count,
                fallbackEvents.GroupBy(item => item.CapabilityId, StringComparer.OrdinalIgnoreCase)
                    .Select(group => new ReportFallbackFrequency(group.Key, group.Count()))
                    .OrderByDescending(item => item.Count)
                    .ThenBy(item => item.CapabilityId, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                fallbackEvents),
            "unavailable",
            new ReportEfficiencySummary(metrics.Count, metrics.Sum(metric => metric.Duration.TotalMilliseconds), repairIterations), details);
    }

    private static string? ReadMetadataString(string? metadataJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            return document.RootElement.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
