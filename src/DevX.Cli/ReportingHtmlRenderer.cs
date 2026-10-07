using System.Globalization;
using System.Net;
using System.Text;

namespace DevX.Cli;

public static class ReportingHtmlRenderer
{
    public static string Metric(CollectedMetric value) => value.Value.HasValue
        ? value.Value.Value.ToString("0.##", CultureInfo.InvariantCulture) : value.State;

    public static string Render(ReportingDetails report)
    {
        var html = new StringBuilder();
        Table("Failure analysis", ["Operation", "Error code", "Count", "Representative reason", "Suggestions"],
            report.FailureAnalysis.Codes.Select(c => new[] { c.Operation, c.Code, c.Count.ToString(), c.Reason ?? "reason not recorded", string.Join("; ", c.Suggestions) }));
        Paragraph("Failure collection", Metric(report.FailureAnalysis.Failures));
        Table("Most-failed names", ["Attempted name", "Count"], report.FailureAnalysis.Names.Select(n => new[] { n.Name, n.Count.ToString() }));
        Table("Repeated failures", ["Workspace", "Session", "Name", "Code", "Count"], report.FailureAnalysis.Repeated.Select(r => new[] { r.Workspace, r.Session ?? "NOT COLLECTED", r.Name, r.Code, r.Count.ToString() }));
        Paragraph("Context savings", report.ContextSavings.Baseline + " Total: " + Metric(report.ContextSavings.EstimatedTokensAvoided)
            + $"; baselines: {report.ContextSavings.Baselines}; no baseline: {report.ContextSavings.NoBaseline}.");
        Table("Context savings by operation", ["Operation", "Baselines", "No baseline", "Estimated tokens avoided"], report.ContextSavings.Operations.Select(g => new[]
            { g.Operation, g.Baselines.ToString(), g.NoBaseline.ToString(), g.EstimatedTokensAvoided.HasValue ? g.EstimatedTokensAvoided.Value.ToString("0.##", CultureInfo.InvariantCulture) : "NOT COLLECTED" }));
        Paragraph("Bypass visibility", $"Bypass candidates: {Metric(report.BypassVisibility.Candidates)}; allowed: {Metric(report.BypassVisibility.Allowed)}; unclassified: {Metric(report.BypassVisibility.Unclassified)}. " + report.BypassVisibility.Caveat);
        Table("Monitoring coverage", ["Workspace", "Session", "Agent label", "Start", "End"], report.BypassVisibility.Coverage.Select(s => new[] { s.Workspace, s.Session ?? "NOT COLLECTED", s.Agent ?? "NOT COLLECTED", s.Start.ToString("u"), s.End.ToString("u") }));
        if (report.BypassVisibility.Coverage.Count == 0) Paragraph("Coverage status", "NOT COLLECTED — no completed monitoring intervals recorded; observed events alone do not prove continuous coverage.");
        Table("Workspace activity", ["Workspace", "Events", "Failures"], report.Workspaces.Select(w => new[] { w.Name, w.Operations.ToString(), w.Failures.ToString() }));
        Table("Latency", ["Operation", "Calls", "Average ms", "p50 ms", "p95 ms"], report.Latency.Select(l => new[]
            { l.Operation, l.Count.ToString(), N(l.AverageMs), N(l.P50Ms), N(l.P95Ms) }));
        Paragraph("Latency collection", Metric(report.LatencyState) + "; percentiles use nearest rank; durations are recorded call durations, not developer time saved.");
        Table("Daily activity (UTC)", ["Day", "Calls"], report.DailyActivity.Select(d => new[] { d.Day, d.Events.ToString() }));
        Table("Slowest ten calls", ["Workspace", "Operation", "Time", "Duration ms", "Agent", "Session"], report.SlowestCalls.Select(e => new[]
            { ReportingDetailsComposer.Workspace(e), e.Operation, e.Timestamp.ToString("u"), N(e.DurationMs), e.Agent ?? "NOT COLLECTED", e.Session ?? "NOT COLLECTED" }));
        Breakdown("Session breakdown", report.Sessions); Breakdown("Agent label breakdown", report.Agents); Breakdown("Task breakdown", report.Tasks);
        Paragraph("Task collection", Metric(report.Availability["Tasks"]));
        Paragraph("Index health", Metric(report.IndexHealthState) + " missing files; snapshot-based, not historical continuous monitoring. Unsupported files skipped: " + Metric(report.UnsupportedFilesSkipped));
        Table("Index health snapshots", ["Workspace", "Observed", "Missing", "Stale", "Without index time", "Paths (first ten)"], report.IndexHealth.Select(h => new[]
            { h.Workspace, h.ObservedAt.ToString("u"), h.MissingFiles.ToString(), h.StaleFiles.HasValue ? h.StaleFiles + (h.FilesWithoutIndexTime > 0 ? " (partial)" : "") : "NOT COLLECTED", h.FilesWithoutIndexTime.ToString(), string.Join("; ", h.MissingPaths.Concat(h.StalePaths)) }));
        Table("Metric availability", ["Metric", "State", "Value", "Reason"], report.Availability.Select(p => new[] { p.Key, p.Value.State, Metric(p.Value), p.Value.Reason ?? "" }));
        return html.ToString();

        void Paragraph(string title, string value) => html.Append("<h2>").Append(E(title)).Append("</h2><section><p>").Append(E(value)).Append("</p></section>");
        void Breakdown(string title, IReadOnlyList<ActivityBreakdown> rows) => Table(title, ["Workspace", "Session", "Agent", "Task", "Calls", "Failures", "Reads", "Validations", "Estimated tokens avoided"],
            rows.Select(r => new[] { r.Workspace, r.Session ?? "—", r.Agent ?? "—", r.Task ?? "NOT COLLECTED", r.Calls.ToString(), r.Failures.ToString(), r.Reads.ToString(), r.Validations.ToString(), Metric(r.EstimatedTokensAvoided) }));
        void Table(string title, string[] headers, IEnumerable<string[]> rows)
        {
            html.Append("<h2>").Append(E(title)).Append("</h2><section><table><thead><tr>");
            foreach (var header in headers) html.Append("<th>").Append(E(header)).Append("</th>");
            html.Append("</tr></thead><tbody>");
            var count = 0;
            foreach (var row in rows) { count++; html.Append("<tr>"); foreach (var cell in row) html.Append("<td>").Append(E(cell)).Append("</td>"); html.Append("</tr>"); }
            if (count == 0) html.Append("<tr><td colspan=\"").Append(headers.Length).Append("\">No recorded rows; see collection status.</td></tr>");
            html.Append("</tbody></table></section>");
        }
    }
    private static string N(double number) => number.ToString("0.##", CultureInfo.InvariantCulture);
    private static string E(string value) => WebUtility.HtmlEncode(value);
}
