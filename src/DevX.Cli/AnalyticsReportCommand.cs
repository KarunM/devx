using System.Globalization;
using System.Text.Json;
using DevX.Infrastructure.Storage.SQLite;
using Microsoft.Data.Sqlite;

namespace DevX.Cli;

public static class AnalyticsReportCommand
{
    public static async Task<int> RunAsync(string connectionString, string[] args)
    {
        try
        {
            var periodText = Option(args, "--period") ?? "30d";
            if (!periodText.EndsWith('d') || !int.TryParse(periodText[..^1], out var days) || days < 1 || days > 3650)
                throw new ArgumentException("--period must be 1d through 3650d.");
            var org = Option(args, "--org");
            var team = Option(args, "--team");
            var inputPaths = Options(args, "--input");
            AnalyticsInput input;
            if (inputPaths.Count > 0)
            {
                var inputs = new List<AnalyticsInput>();
                foreach (var inputPath in inputPaths)
                    inputs.Add(JsonSerializer.Deserialize<AnalyticsInput>(await File.ReadAllTextAsync(inputPath),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new ArgumentException("Invalid report input."));
                if (inputs.Select(i => i.Organization).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                    throw new ArgumentException("Input organizations must match; local exports do not establish organization identity.");
                input = new(inputs[0].Organization, inputs.SelectMany(i => i.Events ?? throw new ArgumentException("Input Events are required.")).ToList(),
                    inputs.SelectMany(i => i.IndexHealth ?? []).ToList(), inputs.SelectMany(i => i.CollectionWindows ?? []).ToList());
            }
            else
            {
                if (org is not null || team is not null)
                    throw new ArgumentException("Organization/team reports require --input <normalized-event-export.json>; local logs do not contain organization membership.");
                input = await LoadLocalAsync(connectionString, Option(args, "--session"));
            }
            if (input.Events is null || input.Events.Any(e => e is null || string.IsNullOrWhiteSpace(e.Id) || string.IsNullOrWhiteSpace(e.Repository) || string.IsNullOrWhiteSpace(e.Operation)
                || !double.IsFinite(e.DurationMs) || e.DurationMs < 0))
                throw new ArgumentException("Each input event requires Id, Repository, and Operation.");
            if (org is not null && !string.Equals(input.Organization, org, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("--org does not match the input organization.");
            if (team is not null && input.Organization is null) throw new ArgumentException("--team requires organization input.");
            var now = DateTimeOffset.UtcNow;
            var exporting = args.Length > 1 && args[1] == "export";
            if (exporting)
            {
                var export = input with
                {
                    Events = input.Events.DistinctBy(e => e.Id).Where(e => e.Timestamp >= now.AddDays(-days) && e.Timestamp <= now).ToList(),
                    CollectionWindows = (input.CollectionWindows ?? []).Where(w => w.End >= now.AddDays(-days) && w.Start <= now)
                        .Select(w => w with { Start = w.Start < now.AddDays(-days) ? now.AddDays(-days) : w.Start, End = w.End > now ? now : w.End }).ToList()
                };
                var destination = Option(args, "--output") ?? "events.json";
                await WriteNewAsync(destination, JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"Wrote event export: {Path.GetFullPath(destination)} ({export.Events.Count} events)");
                return 0;
            }
            var report = AnalyticsReportAggregator.Compose(input, now, TimeSpan.FromDays(days), team, Option(args, "--repo"));
            var format = Option(args, "--format") ?? "html";
            if (format is not ("html" or "json")) throw new ArgumentException("Report export supports --format html or json.");
            var output = Option(args, "--output") ?? (report.Scope == "organization" ? "devx-org-report" : "devx-report") + (format == "json" ? ".json" : ".html");
            var content = format == "json" ? JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) : AnalyticsHtmlRenderer.Render(report);
            await WriteNewAsync(output, content);
            Console.WriteLine($"Wrote {report.Scope} report: {Path.GetFullPath(output)} ({report.Operations} recorded events)");
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or IOException)
        { Console.Error.WriteLine(ex.Message); return 1; }
    }

    public static async Task<AnalyticsInput> LoadLocalAsync(string connectionString, string? session)
    {
        var store = new SqliteStorageInitializer(connectionString);
        await store.InitializeAsync();
        var sessionStore = new SqliteSessionStore(connectionString);
        await sessionStore.InitializeAsync();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.id, COALESCE(r.name, $name), m.agent_label, m.session_id, m.operation, m.timestamp, m.success, m.duration_ticks, m.metadata_json
            FROM operation_metrics m LEFT JOIN sessions s ON s.id = m.session_id LEFT JOIN repositories r ON r.id = s.repository_id
            WHERE (r.root_path = $root OR m.session_id IS NULL) AND ($session IS NULL OR m.session_id = $session) ORDER BY m.id;
            """;
        command.Parameters.AddWithValue("$root", Path.GetFullPath(Environment.CurrentDirectory));
        command.Parameters.AddWithValue("$name", Path.GetFileName(Environment.CurrentDirectory));
        command.Parameters.AddWithValue("$session", (object?)session ?? DBNull.Value);
        var events = new List<AnalyticsEvent>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            events.Add(new AnalyticsEvent(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(Environment.CurrentDirectory))))
                + ":" + reader.GetInt64(0).ToString(CultureInfo.InvariantCulture), reader.GetString(1), null, null,
                reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture), reader.GetInt64(6) == 1,
                TimeSpan.FromTicks(reader.GetInt64(7)).TotalMilliseconds, reader.IsDBNull(8) ? null : reader.GetString(8), Path.GetFullPath(Environment.CurrentDirectory)));
        await reader.DisposeAsync();
        return new AnalyticsInput(null, events, [await IndexHealthCollector.CollectAsync(connectionString, Environment.CurrentDirectory)],
            events.Count == 0 ? [] : [new EventCollectionWindow(Path.GetFullPath(Environment.CurrentDirectory), events.Min(e => e.Timestamp), DateTimeOffset.UtcNow)]);
    }
    private static async Task WriteNewAsync(string path, string content)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(content);
    }
    public static IReadOnlyList<string> Options(string[] args, string name)
    {
        var result = new List<string>();
        for (var i = 0; i < args.Length; i++)
            if (args[i] == name)
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Missing value for {name}.");
                result.Add(args[++i]);
            }
        return result;
    }
    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0) return null;
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"Missing value for {name}.");
        return args[index + 1];
    }
}
