using DevX.Cli;
using Xunit;

namespace DevX.Tests.Cli;

public sealed class AnalyticsReportTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-06T12:00:00Z");
    private static AnalyticsEvent Event(string id, string operation, bool success = true, int daysAgo = 1,
        string repository = "backend", string team = "payments", string? metadata = null)
        => new(id, repository, team, null, "deepseek", "session", operation, Now.AddDays(-daysAgo), success, 10, metadata);

    [Fact]
    public void Aggregator_FiltersPeriodAndTeam_DeduplicatesEvents_SeparatesTrends()
    {
        var events = new[] { Event("1", "test"), Event("1", "test"), Event("2", "test", false),
            Event("3", "build", daysAgo: 40), Event("4", "test", team: "other"), Event("5", "test", daysAgo: -1) };
        var report = AnalyticsReportAggregator.Compose(new("acme", events), Now, TimeSpan.FromDays(30), "payments");
        Assert.Equal(2, report.Operations);
        Assert.Equal(1, report.FailedValidations);
        Assert.Equal(1, report.Trends[0].Operations);
        Assert.Equal(0, report.IdentifiedDevelopers);
        Assert.Equal(1, report.Agents);
    }

    [Fact]
    public void Aggregator_MeasuresOnlyAvailableOutput_HandlesMalformedMetadata()
    {
        var report = AnalyticsReportAggregator.Compose(new(null, [Event("1", "read method", metadata: "{\"ResponseCharacters\":800}"),
            Event("2", "test", false, metadata: "{\"Summary\":\"TIMEOUT after 1s\"}"), Event("3", "read method", metadata: "invalid"),
            Event("4", "read method", metadata: "{\"ResponseCharacters\":\"unknown\"}")]), Now, TimeSpan.FromDays(30));
        Assert.Equal(800, report.ResponseCharacters);
        Assert.Equal(1, report.MeasuredOutputs);
        Assert.Equal(1, report.Timeouts);
        Assert.Contains(report.Limitations, limitation => limitation.Contains("not provider billing"));
    }

    [Fact]
    public void Renderer_EncodesUntrustedLabels_UsesNoScriptsOrExternalResources()
    {
        var report = AnalyticsReportAggregator.Compose(new("<script>alert(1)</script>",
            [Event("1", "test", repository: "<img src=x onerror=alert(1)>")]), Now, TimeSpan.FromDays(30));
        var html = AnalyticsHtmlRenderer.Render(report);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("https://", html);
        Assert.Contains("Validation counts describe runs", html);
    }

    [Fact]
    public void Aggregator_DoesNotRecommendFromSparseFailures()
    {
        var sparse = AnalyticsReportAggregator.Compose(new("acme", [Event("1", "test", false)]), Now, TimeSpan.FromDays(30));
        Assert.Empty(sparse.Recommendations);
        var recurrent = AnalyticsReportAggregator.Compose(new("acme", Enumerable.Range(1, 5).Select(i => Event(i.ToString(), "test", false)).ToList()), Now, TimeSpan.FromDays(30));
        Assert.Single(recurrent.Recommendations);
    }
}
