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
    static async Task<int> SearchAsync(string connectionString, string[] args, string format)
    {
        var implicitSymbol = args.Length >= 2 && args[1] is not ("symbol" or "text") && !args[1].StartsWith("--", StringComparison.Ordinal);
        if (args.Length < (implicitSymbol ? 2 : 4))
        {
            Console.Error.WriteLine("Usage: devx search <query> [--session <id>] | devx search <symbol|text> <query> [--session <id>]. A symbol hit shows the declaration line (bases for a class) and path:lines; for who-calls/where-used use devx callers|references instead of text search.");
            return 1;
        }

        var searchKind = implicitSymbol ? "symbol" : args[1];
        // Several queries in one call: `devx search text "a" "b"` is the alternation `a|b`, so a
        // guess that missed costs nothing instead of another round trip. In the 1.6.11 transcripts
        // 34 of the 40 chained `search text` calls were a different word tried one at a time.
        var queryParts = PositionalQueryParts(args, implicitSymbol ? 1 : 2);
        if (queryParts.Count == 0)
        {
            Console.Error.WriteLine("Usage: devx search <query> [--session <id>] | devx search <symbol|text> <query> [<query> ...] [--session <id>]. A symbol hit shows the declaration line (bases for a class) and path:lines; for who-calls/where-used use devx callers|references instead of text search.");
            return 1;
        }

        var query = searchKind == "text" ? string.Join('|', queryParts) : queryParts[0];
        var sessionId = GetOption(args, "--session");
        if (sessionId is null)
        {
            Console.Error.WriteLine("Missing --session.");
            return 1;
        }

        var store = new SqliteStorageInitializer(connectionString);
        await store.InitializeAsync();
        var engine = new ProtocolEngine(new SearchService(store, store), store, store);
        var request = new ProtocolRequest(
            "1.0",
            searchKind == "symbol" ? ProtocolOperations.SearchSymbol : ProtocolOperations.SearchText,
            sessionId,
            JsonSerializer.SerializeToElement(new { query }));

        var started = DateTimeOffset.UtcNow;
        var response = await engine.ExecuteAsync(request);
        if (implicitSymbol && response.Success && response.Data is { ValueKind: JsonValueKind.Object } data && TryGetPropertyIgnoreCase(data, "Symbols", out var matches) && matches.ValueKind == JsonValueKind.Array && matches.GetArrayLength() == 0)
        {
            Console.Error.WriteLine("(no symbol match; showing text matches)");
            response = await engine.ExecuteAsync(new ProtocolRequest("1.0", ProtocolOperations.SearchText, sessionId, JsonSerializer.SerializeToElement(new { query })));
            searchKind = "text";
        }
        // Search and read in one call. Only for a SINGLE hit: several equal hits must always be
        // listed, never silently resolved to one. The hit line is printed first, so the agent
        // keeps the identity (qualified name, path, span) as well as the source. Agent format
        // only -- --format json keeps its exact shape.
        string? autoRead = null;
        if (format == "agent" && searchKind == "symbol" && response.Success && response.Data is { ValueKind: JsonValueKind.Object } searchData && TryGetPropertyIgnoreCase(searchData, "Symbols", out var singleHits) && singleHits.ValueKind == JsonValueKind.Array && singleHits.GetArrayLength() == 1)
        {
            var hit = singleHits[0];
            var id = JsonPropertyText(hit, "Id");
            var kind = JsonPropertyText(hit, "Kind");
            if (id.Length > 0)
            {
                var operation = kind is "Class" or "Type" or "Record" or "Interface" or "Struct" or "Enum" ? "READ_TYPE" : "READ_METHOD";
                var read = await engine.ExecuteAsync(new ProtocolRequest("1.0", operation, sessionId, JsonSerializer.SerializeToElement(new { id })));
                if (read.Success) autoRead = AutoReadText(read);
            }
        }

        // Every content hit is named for the symbol it sits in -- the one thing a plain `rg -n`
        // cannot say, and what makes the next call a `read <symbol>` instead of a guess.
        IReadOnlyDictionary<(string FileId, int Line), string>? enclosingSymbols = null;
        if (format == "agent" && searchKind == "text" && response.Success && response.Data is { ValueKind: JsonValueKind.Object } hitData && TryGetPropertyIgnoreCase(hitData, "ContentMatches", out var contentHits) && contentHits.ValueKind == JsonValueKind.Array)
        {
            var points = contentHits.EnumerateArray()
                .Take(10)
                .Select(item => (FileId: JsonPropertyText(item, "FileId"), Line: int.TryParse(JsonPropertyText(item, "Line"), out var at) ? at : 0))
                .Where(point => point.FileId.Length > 0 && point.Line > 0)
                .ToList();
            if (points.Count > 0)
            {
                enclosingSymbols = await store.FindEnclosingSymbolsAsync(points);
            }
        }

        await RecordMetricAsync(store, new OperationMetric($"search {searchKind}", started, DateTimeOffset.UtcNow - started, response.Success, sessionId, OperationMetadata(response, query), GetAgentLabel(args)));
        WriteResponse(response, format, enclosingSymbols);
        if (autoRead is not null) Console.WriteLine(autoRead);
        return response.Success ? 0 : 1;
    }

    // Read/search history is best-effort: a read-only session database must not fail the command.
    static async Task RecordMetricAsync(ISessionStore store, OperationMetric metric)
    {
        try { await store.AddOperationMetricAsync(metric); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.Error.WriteLine("history not recorded: " + error.Message);
        }
    }

    static List<string> PositionalQueryParts(string[] args, int start)
    {
        // The options `search` accepts that take a value, so their value is never mistaken for a
        // query. Everything else that starts with `-` is a flag.
        string[] valueOptions = ["--session", "--agent", "--task", "--format"];
        var parts = new List<string>();
        for (var i = start; i < args.Length; i++)
        {
            if (args[i].StartsWith('-'))
            {
                if (valueOptions.Contains(args[i], StringComparer.Ordinal))
                {
                    i++;                       // skip the option's value
                }

                continue;
            }

            parts.Add(args[i]);
        }

        return parts;
    }
}
