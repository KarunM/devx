using System.Globalization;
using System.Net;
using System.Text;

namespace DevX.Cli;

public static class AnalyticsHtmlRenderer
{
    public static string Render(AnalyticsReport report)
    {
        var html = new StringBuilder("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>DevX engineering report</title><style>body{font:16px system-ui,sans-serif;color:#172333;background:#f4f6fa;margin:0}main{max-width:1100px;margin:auto;padding:40px 24px}h1{font-size:36px}h2{margin-top:36px}p,li{line-height:1.6}.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:14px}.card,section{background:white;border:1px solid #dde3eb;border-radius:10px;padding:20px}.card strong{display:block;font-size:30px;color:#174b7c}table{border-collapse:collapse;width:100%}th,td{text-align:left;padding:12px;border-bottom:1px solid #dde3eb}th{font-size:13px;color:#526174}footer{margin-top:40px;color:#526174}@media print{body{background:white}main{padding:0}.card,section{break-inside:avoid}} </style></head><body><main>");
        html.Append("<h1>DevX engineering report</h1><p>").Append(E(report.Organization ?? "Local developer workspace"))
            .Append(" · ").Append(E(report.Scope)).Append("</p><p>")
            .Append(E(report.PeriodStart.ToString("u"))).Append(" – ").Append(E(report.PeriodEnd.ToString("u"))).Append("</p>");
        html.Append("<div class=\"cards\">");
        MetricCard("Repositories with events", "Repositories", report.Repositories);
        StatusCard("Teams identified", report.Teams > 0 ? report.Teams.ToString() + " (input attribution)" : "UNAVAILABLE");
        StatusCard("Developers identified", report.IdentifiedDevelopers > 0 ? report.IdentifiedDevelopers.ToString() + " (input attribution)" : "UNAVAILABLE"); MetricCard("Agent labels", "Agents", report.Agents);
        MetricCard("Sessions with events", "Sessions", report.Sessions); MetricCard("Recorded events", "Operations", report.Operations);
        html.Append("</div><h2>Usage and validation reliability</h2><div class=\"cards\">");
        MetricCard("Searches", "Searches", report.Searches); MetricCard("Read events", "Reads", report.Reads); MetricCard("Patch events", "Edits", report.Edits);
        MetricCard("Validation runs", "Validations", report.Validations); MetricCard("Failed validations", "FailedValidations", report.FailedValidations);
        if (report.Reporting is { } coverage) StatusCard("Recorded fallbacks", ReportingHtmlRenderer.Metric(coverage.Availability["RecordedFallbacks"]));
        else Card("Recorded fallbacks", report.Fallbacks);
        MetricCard("Timeouts", "Timeouts", report.Timeouts);
        html.Append("</div><p>Validation success rate: ")
            .Append(report.Validations == 0 ? "NOT COLLECTED (no validation runs)" : ((report.Validations - report.FailedValidations) * 100d / report.Validations).ToString("0.0", CultureInfo.InvariantCulture) + "%")
            .Append(". Validation counts describe runs, not individual test cases.</p>");
        Table("Repository activity", report.RepositoryActivity);
        Table("Agent activity", report.AgentActivity);
        Table("Operation activity", report.OperationActivity);
        html.Append("<h2>Period comparison</h2><section><table><thead><tr><th>Period</th><th>Events</th><th>Validations</th><th>Failed validations</th></tr></thead><tbody>");
        foreach (var trend in report.Trends)
        {
            var state = report.Reporting?.Availability[trend.Period.StartsWith("Previous") ? "PreviousPeriod" : "CurrentPeriod"];
            var missing = state is { Value: null };
            html.Append("<tr><td>").Append(E(trend.Period)).Append("</td><td>").Append(missing ? state!.State : trend.Operations.ToString())
                .Append("</td><td>").Append(missing ? state!.State : trend.Validations.ToString()).Append("</td><td>").Append(missing ? state!.State : trend.FailedValidations.ToString()).Append("</td></tr>");
        }
        html.Append("</tbody></table></section><h2>Operational efficiency</h2><section><p>Measured output characters: ")
            .Append(report.MeasuredOutputs == 0 ? "NOT COLLECTED" : report.ResponseCharacters.ToString()).Append(" across ").Append(report.MeasuredOutputs)
            .Append(" events. Estimated output tokens: ").Append(report.MeasuredOutputs == 0 ? "NOT COLLECTED" : ((long)Math.Ceiling(report.ResponseCharacters / 4d)).ToString())
            .Append(". Avoided context and token savings: ").Append(report.Reporting is null ? "NOT COLLECTED" : ReportingHtmlRenderer.Metric(report.Reporting.ContextSavings.EstimatedTokensAvoided)).Append(" (estimated whole-file baseline).</p></section>");
        if (report.Reporting is not null) html.Append(ReportingHtmlRenderer.Render(report.Reporting));
        html.Append("<h2>Recommendations</h2><section>");
        List(report.Recommendations.Count == 0 ? ["No recommendation meets the evidence threshold for this period."] : report.Recommendations);
        html.Append("</section><h2>Data coverage and governance</h2><section>");
        List(report.Limitations);
        html.Append("</section><footer>Generated ").Append(E(report.GeneratedAt.ToString("u")))
            .Append(" · DevX report schema ").Append(E(report.SchemaVersion)).Append(" · Self-contained; no network resources.</footer></main></body></html>");
        return html.ToString();

        void Card(string label, long value) => html.Append("<div class=\"card\"><strong>").Append(value).Append("</strong>").Append(E(label)).Append("</div>");
        void StatusCard(string label, string value) => html.Append("<div class=\"card\"><strong>").Append(E(value)).Append("</strong>").Append(E(label)).Append("</div>");
        void MetricCard(string label, string key, int value)
        { if (report.Reporting is { } r) StatusCard(label, ReportingHtmlRenderer.Metric(r.Availability[key])); else Card(label, value); }
        void List(IReadOnlyList<string> values)
        { html.Append("<ul>"); foreach (var value in values) html.Append("<li>").Append(E(value)).Append("</li>"); html.Append("</ul>"); }
        void Table(string title, IReadOnlyList<AnalyticsGroup> groups)
        {
            html.Append("<h2>").Append(E(title)).Append("</h2><section><table><thead><tr><th>Name</th><th>Events</th><th>Failures</th><th>Validations</th><th>Failed validations</th></tr></thead><tbody>");
            foreach (var group in groups)
                html.Append("<tr><td>").Append(E(group.Name)).Append("</td><td>").Append(group.Operations)
                    .Append("</td><td>").Append(group.Failures).Append("</td><td>").Append(group.Validations)
                    .Append("</td><td>").Append(group.FailedValidations).Append("</td></tr>");
            html.Append("</tbody></table></section>");
        }
    }
    private static string E(string value) => WebUtility.HtmlEncode(value);
}
