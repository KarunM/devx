using System.Text.Json;

namespace DevX.Cli;

// The export contract is independent of storage and HTML rendering.
public sealed record AnalyticsEvent(string Id, string Repository, string? Team, string? Developer,
    string? Agent, string? Session, string Operation, DateTimeOffset Timestamp, bool Success,
    double DurationMs, string? MetadataJson, string? Workspace = null, string? Task = null);
public sealed record AnalyticsInput(string? Organization, IReadOnlyList<AnalyticsEvent> Events,
    IReadOnlyList<IndexHealthSnapshot>? IndexHealth = null, IReadOnlyList<EventCollectionWindow>? CollectionWindows = null);
public sealed record EventCollectionWindow(string Workspace, DateTimeOffset Start, DateTimeOffset End);
public sealed record AnalyticsGroup(string Name, int Operations, int Failures, int Validations, int FailedValidations);
public sealed record AnalyticsTrend(string Period, int Operations, int Validations, int FailedValidations);
public sealed record AnalyticsReport(string SchemaVersion, string Scope, string? Organization,
    DateTimeOffset GeneratedAt, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd,
    int Repositories, int Teams, int IdentifiedDevelopers, int Agents, int Sessions,
    int Operations, int Searches, int Reads, int Edits, int Validations, int FailedValidations,
    int Fallbacks, int Timeouts, long ResponseCharacters, int MeasuredOutputs,
    IReadOnlyList<AnalyticsGroup> RepositoryActivity, IReadOnlyList<AnalyticsGroup> AgentActivity,
    IReadOnlyList<AnalyticsGroup> OperationActivity, IReadOnlyList<AnalyticsTrend> Trends,
    IReadOnlyList<string> Recommendations, IReadOnlyList<string> Limitations, ReportingDetails? Reporting = null);

public static class AnalyticsReportAggregator
{
    public static AnalyticsReport Compose(AnalyticsInput input, DateTimeOffset now, TimeSpan period,
        string? team = null, string? repository = null)
    {
        if (period <= TimeSpan.Zero) throw new ArgumentException("Period must be positive.");
        var filtered = input.Events.DistinctBy(e => e.Id).Where(e =>
            (team is null || string.Equals(team, e.Team, StringComparison.OrdinalIgnoreCase)) &&
            (repository is null || string.Equals(repository, e.Repository, StringComparison.OrdinalIgnoreCase))).ToList();
        var start = now - period;
        var current = filtered.Where(e => e.Timestamp >= start && e.Timestamp <= now).ToList();
        var previous = filtered.Where(e => e.Timestamp >= start - period && e.Timestamp < start).ToList();
        var repositories = Groups(current, e => e.Repository);
        var details = ReportingDetailsComposer.Compose(input with { Events = filtered }, current, start, now);
        var availability = (Dictionary<string, CollectedMetric>)details.Availability;
        bool recorded = current.Count > 0 || (input.CollectionWindows ?? []).Any(w => w.Start <= start && w.End >= now);
        CollectedMetric CountMetric(double n) => recorded ? CollectedMetric.Count(n) : CollectedMetric.Missing("No recorded event-history coverage for this period.");
        availability["PreviousPeriod"] = previous.Count > 0 || (input.CollectionWindows ?? []).Any(w => w.Start <= start - period && w.End >= start)
            ? CollectedMetric.Count(previous.Count) : CollectedMetric.Missing("Earlier history was not collected in the supplied exports.");
        availability["CurrentPeriod"] = CountMetric(current.Count);
        availability["Operations"] = CountMetric(current.Count);
        availability["Repositories"] = CountMetric(repositories.Count);
        availability["Teams"] = current.Any(e => e.Team is not null) ? CollectedMetric.Count(Count(current.Select(e => e.Team))) : CollectedMetric.Unavailable("No authenticated team identity source; optional input labels are unverified.");
        availability["IdentifiedDevelopers"] = current.Any(e => e.Developer is not null) ? CollectedMetric.Count(Count(current.Select(e => e.Developer))) : CollectedMetric.Unavailable("No authenticated developer identity source; optional input labels are unverified.");
        availability["Agents"] = current.Any(e => e.Agent is not null) ? CollectedMetric.Count(Count(current.Select(e => e.Agent))) : CollectedMetric.Missing("Agent labels were not recorded.");
        availability["Sessions"] = current.Any(e => e.Session is not null) ? CollectedMetric.Count(Count(current.Select(e => e.Session))) : CollectedMetric.Missing("Session IDs were not recorded.");
        availability["Searches"] = CountMetric(current.Count(e => e.Operation.StartsWith("search ")));
        availability["Reads"] = CountMetric(current.Count(e => e.Operation.StartsWith("read ")));
        availability["Edits"] = CountMetric(current.Count(IsEdit));
        availability["Validations"] = CountMetric(current.Count(IsValidation));
        availability["FailedValidations"] = CountMetric(current.Count(e => IsValidation(e) && !e.Success));
        var recommendations = repositories.Where(g => g.Validations >= 5 && g.FailedValidations * 1d / g.Validations >= .25)
            .Select(g => $"{g.Name}: {g.FailedValidations} of {g.Validations} validation runs failed; review recurring failures.").Take(3).ToList();
        if (current.Any(e => IsEdit(e)) && !current.Any(IsValidation))
            recommendations.Add("Edits were recorded without any build or test runs in this period; review validation practices.");
        if (details.FailureAnalysis.Repeated.Count > 0)
            recommendations.Add("Repeated name/error failures were recorded; review the failure analysis before retrying the same lookup.");
        if (details.IndexHealth.Any(h => h.MissingFiles > 0 || h.StaleFiles > 0))
            recommendations.Add("Missing or stale indexed files were observed; refresh the affected workspace index.");
        long characters = 0;
        var measured = 0;
        var timeouts = 0;
        foreach (var e in current)
        {
            if (string.IsNullOrWhiteSpace(e.MetadataJson)) continue;
            try
            {
                using var doc = JsonDocument.Parse(e.MetadataJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                if (doc.RootElement.TryGetProperty("ResponseCharacters", out var size) && size.ValueKind == JsonValueKind.Number && size.TryGetInt64(out var value) && value >= 0)
                { characters += value; measured++; }
                if (doc.RootElement.TryGetProperty("Summary", out var summary) && summary.ValueKind == JsonValueKind.String
                    && summary.GetString()!.StartsWith("TIMEOUT", StringComparison.OrdinalIgnoreCase)) timeouts++;
            }
            catch (JsonException) { }
        }
        availability["Timeouts"] = current.Any(e => ReportingDetailsComposer.Property(e, "Summary") is not null)
            ? CollectedMetric.Count(timeouts) : CollectedMetric.Missing("Timeout summary metadata was not collected in this period.");
        availability["ResponseCharacters"] = availability["OutputCharacters"];
        availability["MeasuredOutputs"] = measured > 0 ? CollectedMetric.Count(measured) : CollectedMetric.Missing("No output measurements recorded.");
        availability["Fallbacks"] = availability["RecordedFallbacks"];
        return new AnalyticsReport("1.0", input.Organization is null ? "developer" : "organization", input.Organization,
            now, start, now, repositories.Count, Count(current.Select(e => e.Team)), Count(current.Select(e => e.Developer)),
            Count(current.Select(e => e.Agent)), Count(current.Select(e => e.Session)), current.Count,
            current.Count(e => e.Operation.StartsWith("search ", StringComparison.Ordinal)),
            current.Count(e => e.Operation.StartsWith("read ", StringComparison.Ordinal)), current.Count(IsEdit),
            current.Count(IsValidation), current.Count(e => IsValidation(e) && !e.Success),
            current.Count(e => e.Operation == "fallback"), timeouts, characters, measured,
            repositories, Groups(current, e => e.Agent ?? "Unattributed"), Groups(current, e => e.Operation),
            [Trend("Previous equal-length period", previous), Trend("Current period", current)], recommendations.Take(3).ToList(),
            ["Counts represent recorded events, not verified developer productivity. Internal read events and CLI calls may both be logged.",
             "Agent labels are caller supplied; they are not authenticated identities. Developers and teams require explicit input attribution.",
             "Output characters are measured where available. Tokens are estimates (characters / 4), not provider billing or measured savings.",
             "Module hotspots, handoffs, merge outcomes, tests-before-change, policy violations, restricted access, and secret detections are unavailable with the current event schema.",
             "Organization input is a local aggregation preview. Identity, attribution, and input provenance are unverified; governance guarantees are unavailable."], details);
    }

    private static int Count(IEnumerable<string?> values) => values.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).Count();
    private static bool IsValidation(AnalyticsEvent e) => e.Operation is "build" or "test";
    private static bool IsEdit(AnalyticsEvent e) => e.Operation is "patch" or "patch apply";
    private static AnalyticsTrend Trend(string label, List<AnalyticsEvent> events)
        => new(label, events.Count, events.Count(IsValidation), events.Count(e => IsValidation(e) && !e.Success));
    private static IReadOnlyList<AnalyticsGroup> Groups(List<AnalyticsEvent> events, Func<AnalyticsEvent, string> key)
        => events.GroupBy(key).Select(g => new AnalyticsGroup(g.Key, g.Count(), g.Count(e => !e.Success),
            g.Count(IsValidation), g.Count(e => IsValidation(e) && !e.Success)))
            .OrderByDescending(g => g.Operations).ThenBy(g => g.Name, StringComparer.Ordinal).ToList();
}
