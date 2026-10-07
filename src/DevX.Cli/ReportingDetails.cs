using System.Text.Json;

namespace DevX.Cli;

public sealed record CollectedMetric(string State, double? Value, string? Reason = null)
{
    public static CollectedMetric Count(double value) => new(value == 0 ? "ZERO" : "COLLECTED", value);
    public static CollectedMetric Missing(string reason) => new("NOT COLLECTED", null, reason);
    public static CollectedMetric Unavailable(string reason) => new("UNAVAILABLE", null, reason);
}
public sealed record FailureCode(string Operation, string Code, int Count, string? Reason, IReadOnlyList<string> Suggestions);
public sealed record FailedName(string Name, int Count);
public sealed record RepeatedFailure(string Workspace, string? Session, string Name, string Code, int Count);
public sealed record FailureAnalysis(CollectedMetric Failures, IReadOnlyList<FailureCode> Codes,
    IReadOnlyList<FailedName> Names, IReadOnlyList<RepeatedFailure> Repeated);
public sealed record SavingsGroup(string Operation, int Baselines, int NoBaseline, double? EstimatedTokensAvoided);
public sealed record ContextSavings(CollectedMetric EstimatedTokensAvoided, int Baselines, int NoBaseline,
    IReadOnlyList<SavingsGroup> Operations, string Baseline = "Estimated tokens avoided versus a whole-file-read baseline (characters / 4); not billing or proven savings.");
public sealed record MonitoringSpan(string Workspace, string? Session, string? Agent, DateTimeOffset Start, DateTimeOffset End);
public sealed record BypassVisibility(CollectedMetric Candidates, CollectedMetric Allowed, CollectedMetric Unclassified,
    IReadOnlyList<MonitoringSpan> Coverage, string Caveat = "Observed shell usage only. Caller-reported coverage and agent labels are not authenticated proof of compliance.");
public sealed record LatencyGroup(string Operation, int Count, double AverageMs, double P50Ms, double P95Ms);
public sealed record DailyActivity(string Day, int Events);
public sealed record ActivityBreakdown(string Workspace, string? Session, string? Agent, string? Task, int Calls,
    int Failures, int Reads, int Validations, CollectedMetric EstimatedTokensAvoided);
public sealed record IndexHealthSnapshot(string Workspace, DateTimeOffset ObservedAt, int MissingFiles,
    int? StaleFiles, int FilesWithoutIndexTime, IReadOnlyList<string> MissingPaths, IReadOnlyList<string> StalePaths);
public sealed record ReportingDetails(FailureAnalysis FailureAnalysis, ContextSavings ContextSavings,
    BypassVisibility BypassVisibility, CollectedMetric LatencyState, IReadOnlyList<LatencyGroup> Latency,
    IReadOnlyList<DailyActivity> DailyActivity, IReadOnlyList<AnalyticsEvent> SlowestCalls,
    IReadOnlyList<ActivityBreakdown> Sessions, IReadOnlyList<ActivityBreakdown> Agents, IReadOnlyList<ActivityBreakdown> Tasks,
    IReadOnlyList<AnalyticsGroup> Workspaces, IReadOnlyList<IndexHealthSnapshot> IndexHealth,
    CollectedMetric IndexHealthState, CollectedMetric UnsupportedFilesSkipped,
    IReadOnlyDictionary<string, CollectedMetric> Availability);

public static class ReportingDetailsComposer
{
    public static ReportingDetails Compose(AnalyticsInput input, IReadOnlyList<AnalyticsEvent> events, DateTimeOffset start, DateTimeOffset end)
    {
        var failures = events.Where(e => !e.Success).Select(e => new
        {
            Event = e, Code = Text(e, "ErrorCode") ?? "reason not recorded",
            Name = Text(e, "AttemptedName") ?? "name not recorded", Reason = Text(e, "FailureReason"),
            Suggestions = Strings(e, "Suggestions")
        }).ToList();
        var codes = failures.GroupBy(f => (f.Event.Operation, f.Code)).Select(g =>
        {
            var representative = g.FirstOrDefault(f => f.Reason is not null) ?? g.First();
            return new FailureCode(g.Key.Operation, g.Key.Code, g.Count(), representative.Reason, representative.Suggestions);
        }).OrderByDescending(g => g.Count).ThenBy(g => g.Operation).ThenBy(g => g.Code).ToList();
        var analysis = new FailureAnalysis(events.Count == 0 ? CollectedMetric.Missing("No operation events in this period.") : CollectedMetric.Count(failures.Count), codes,
            failures.Where(f => f.Name != "name not recorded").GroupBy(f => f.Name).Select(g => new FailedName(g.Key, g.Count()))
                .OrderByDescending(g => g.Count).ThenBy(g => g.Name).Take(20).ToList(),
            failures.Where(f => f.Event.Session is not null && f.Name != "name not recorded")
                .GroupBy(f => (Workspace(f.Event), f.Event.Session, f.Name, f.Code)).Where(g => g.Count() >= 2)
                .Select(g => new RepeatedFailure(g.Key.Item1, g.Key.Session, g.Key.Name, g.Key.Code, g.Count())).OrderByDescending(g => g.Count).ToList());
        var outerIds = events.Where(e => !Internal(e)).Select(e => Text(e, "ReadEventId")).Where(id => id is not null).ToHashSet();
        var canonical = events.Where(e => !Internal(e) || Text(e, "ReadEventId") is { } id && !outerIds.Contains(id)).ToList();
        var reads = canonical.Where(e => e.Operation.StartsWith("read ")).ToList();
        var savingsGroups = reads.GroupBy(e => e.Operation).Select(g => new SavingsGroup(g.Key,
            g.Count(e => Savings(e).HasValue), g.Count(e => !Savings(e).HasValue), g.Any(e => Savings(e).HasValue) ? g.Sum(e => Savings(e) ?? 0) : null)).OrderBy(g => g.Operation).ToList();
        var baselines = savingsGroups.Sum(g => g.Baselines);
        var savings = new ContextSavings(baselines == 0 ? CollectedMetric.Missing("No reads with recorded whole-file and returned-character baselines.")
            : CollectedMetric.Count(savingsGroups.Sum(g => g.EstimatedTokensAvoided ?? 0)), baselines, reads.Count - baselines, savingsGroups);
        var spans = input.Events.Where(e => e.Operation == "fallback coverage").Select(e => Span(e))
            .Where(s => s is not null && s.End >= start && s.Start <= end && s.End >= s.Start)
            .Select(s => s! with { Start = s.Start < start ? start : s.Start, End = s.End > end ? end : s.End }).Distinct().ToList();
        var observed = events.Where(e => e.Operation == "fallback observe").ToList();
        CollectedMetric Observations(string category) => spans.Count == 0 && observed.Count == 0
            ? CollectedMetric.Missing("Shell monitoring was not recorded for this period.")
            : CollectedMetric.Count(observed.Count(e => Text(e, "Classification") == category));
        var bypass = new BypassVisibility(Observations("bypass candidate"), Observations("allowed"), Observations("unclassified"), spans);
        var timed = canonical;
        var latency = timed.GroupBy(e => e.Operation).Select(g => new LatencyGroup(g.Key, g.Count(), g.Average(e => e.DurationMs),
            Percentile(g.Select(e => e.DurationMs), .5), Percentile(g.Select(e => e.DurationMs), .95))).OrderBy(g => g.Operation).ToList();
        IReadOnlyList<ActivityBreakdown> Breakdown(Func<AnalyticsEvent, (string Workspace, string? Session, string? Agent, string? Task)> key)
            => timed.GroupBy(key).Select(g => new ActivityBreakdown(g.Key.Workspace, g.Key.Session, g.Key.Agent, g.Key.Task,
                g.Count(), g.Count(e => !e.Success), g.Count(e => e.Operation.StartsWith("read ")), g.Count(e => e.Operation is "build" or "test"),
                g.Any(e => Savings(e).HasValue) ? CollectedMetric.Count(g.Sum(e => Savings(e) ?? 0)) : CollectedMetric.Missing("No read baselines recorded.")))
                .OrderByDescending(g => g.Calls).ToList();
        var health = (input.IndexHealth ?? []).Where(h => h.ObservedAt >= start && h.ObservedAt <= end)
            .GroupBy(h => h.Workspace).Select(g => g.OrderByDescending(h => h.ObservedAt).First()).ToList();
        var skipped = events.Select(e => Number(e, "UnsupportedFilesSkipped")).Where(v => v.HasValue).ToList();
        return new(analysis, savings, bypass,
            timed.Count == 0 ? CollectedMetric.Missing("No timed calls in this period.") : CollectedMetric.Count(timed.Count), latency,
            timed.GroupBy(e => e.Timestamp.UtcDateTime.ToString("yyyy-MM-dd")).OrderBy(g => g.Key).Select(g => new DailyActivity(g.Key, g.Count())).ToList(),
            timed.OrderByDescending(e => e.DurationMs).ThenBy(e => e.Id).Take(10).ToList(),
            Breakdown(e => (Workspace(e), e.Session, null, null)), Breakdown(e => (Workspace(e), null, e.Agent, null)),
            Breakdown(e => (Workspace(e), null, null, e.Task ?? Text(e, "TaskLabel"))),
            events.GroupBy(Workspace).Select(g => new AnalyticsGroup(g.Key, g.Count(), g.Count(e => !e.Success),
                g.Count(e => e.Operation is "build" or "test"), g.Count(e => e.Operation is "build" or "test" && !e.Success))).OrderBy(g => g.Name).ToList(),
            health, health.Count == 0 ? CollectedMetric.Missing("No index-health snapshot collected in this period.") : CollectedMetric.Count(health.Sum(h => h.MissingFiles)),
            skipped.Count == 0 ? CollectedMetric.Missing("No skip counters recorded in this period.") : CollectedMetric.Count(skipped.Sum(v => v!.Value)),
            new Dictionary<string, CollectedMetric>
            {
                ["AuthenticatedIdentity"] = CollectedMetric.Unavailable("No authenticated identity source."),
                ["PolicyEnforcement"] = CollectedMetric.Unavailable("No policy enforcement source."),
                ["MergeOutcomes"] = CollectedMetric.Unavailable("No merge outcome source."),
                ["SecretDetection"] = CollectedMetric.Unavailable("No secret detection source."),
                ["OutputCharacters"] = events.Any(e => Number(e, "ResponseCharacters").HasValue) ? CollectedMetric.Count(events.Sum(e => Number(e, "ResponseCharacters") ?? 0)) : CollectedMetric.Missing("No measured outputs."),
                ["RecordedFallbacks"] = events.Any(e => e.Operation == "fallback") ? CollectedMetric.Count(events.Count(e => e.Operation == "fallback")) : CollectedMetric.Missing("No manual fallback records; absence is not compliance."),
                ["Tasks"] = timed.Any(e => e.Task is not null || Text(e, "TaskLabel") is not null) ? CollectedMetric.Count(timed.Count(e => e.Task is not null || Text(e, "TaskLabel") is not null)) : CollectedMetric.Missing("No task labels recorded.")
            });
    }
    public static string Workspace(AnalyticsEvent e) => e.Workspace ?? e.Repository;
    public static bool Internal(AnalyticsEvent e) => Text(e, "EventOrigin") == "internal-read";
    public static double? Savings(AnalyticsEvent e)
    {
        if (!e.Success || !e.Operation.StartsWith("read ")) return null;
        var whole = Number(e, "WholeFileCharacters"); var returned = Number(e, "ReturnedCharacters");
        return whole.HasValue && returned.HasValue ? Math.Max(0, whole.Value - returned.Value) / 4d : null;
    }
    public static JsonElement? Property(AnalyticsEvent e, string name)
    {
        try { using var d = JsonDocument.Parse(e.MetadataJson ?? "null"); return d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty(name, out var p) ? p.Clone() : null; }
        catch (JsonException) { return null; }
    }
    public static string? Text(AnalyticsEvent e, string name) => Property(e, name) is { ValueKind: JsonValueKind.String } p
        && !string.IsNullOrWhiteSpace(p.GetString()) ? p.GetString() : null;
    public static double? Number(AnalyticsEvent e, string name) => Property(e, name) is { ValueKind: JsonValueKind.Number } p && p.TryGetDouble(out var n) && double.IsFinite(n) && n >= 0 ? n : null;
    private static IReadOnlyList<string> Strings(AnalyticsEvent e, string name) => Property(e, name) is { ValueKind: JsonValueKind.Array } p
        ? p.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList() : [];
    private static MonitoringSpan? Span(AnalyticsEvent e) => DateTimeOffset.TryParse(Text(e, "CoverageStart"), out var start)
        && DateTimeOffset.TryParse(Text(e, "CoverageEnd"), out var end) ? new(Workspace(e), e.Session, e.Agent, start, end) : null;
    private static double Percentile(IEnumerable<double> values, double percentile)
    { var ordered = values.Order().ToArray(); return ordered[Math.Max(0, (int)Math.Ceiling(ordered.Length * percentile) - 1)]; }
}
