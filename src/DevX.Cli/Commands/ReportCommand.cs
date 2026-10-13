using System.Text.Json;
using DevX.Application;
using DevX.Domain;
using DevX.Cli;
using DevX.Infrastructure.Storage.SQLite;
using DevX.Indexer;
using DevX.Protocol;
using Microsoft.Data.Sqlite;

static partial class Program
{
    static async Task<int> ReportAsync(string connectionString, string[] args, string format)
    {
        if (args.Length > 1 && args[1] is "html" or "export")
            return await AnalyticsReportCommand.RunAsync(connectionString, args);

        var sessionId = GetOption(args, "--session");
        if (sessionId is null)
        {
            Console.Error.WriteLine("Missing --session. Report requires explicit session selection.");
            return 1;
        }

        var store = new SqliteStorageInitializer(connectionString);
        await store.InitializeAsync();
        var session = await store.GetSessionAsync(new GetSessionRequest(new SessionId(Guid.Parse(sessionId))));
        if (session is null)
        {
            Console.Error.WriteLine("Session not found.");
            return 1;
        }

        var metrics = await store.GetOperationMetricsAsync(session.Id);
        var report = await new DevXReportComposer().ComposeAsync(session, metrics, store);

        if (format == "json")
        {
            if (args.Contains("--anonymize", StringComparer.Ordinal)) report = ReportAnonymizer.Anonymize(report);
            var json = JsonSerializer.Serialize(report);
            var blockedPath = GetOption(args, "--blocked-terms");
            if (blockedPath is not null)
            {
                IReadOnlyList<string> matches;
                try { matches = await BlockedTerms.FindMatchesAsync(blockedPath, json); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { Console.Error.WriteLine($"Could not read blocked-terms file: {ex.Message}"); return 2; }
                var count = matches.Sum(term => CountReportTerm(json, term));
                if (count > 0) { Console.Error.WriteLine($"blocked terms found: {count} ({matches.Count} terms)"); return 3; }
            }
            Console.WriteLine(json);
            return 0;
        }

        Console.WriteLine("Session Summary");
        Console.WriteLine($"Session: {session.Id.Value}");
        Console.WriteLine($"Repository: {session.RepositoryId.Value}");
        Console.WriteLine($"Searches: {session.SearchHistory.Count}");
        Console.WriteLine();
        Console.WriteLine("DevX Operation Log");
        foreach (var metric in report.OperationLog)
        {
            Console.WriteLine($"{metric.Operation} | {metric.Timestamp:O} | {metric.DurationMs}ms | {metric.Success}");
        }

        return 0;
    }

    static int CountReportTerm(string content, string term)
    {
        var count = 0;
        var index = 0;
        while ((index = content.IndexOf(term, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        { count++; index += term.Length; }
        return count;
    }
}
