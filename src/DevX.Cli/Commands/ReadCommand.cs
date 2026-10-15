using System.Text.Json;
using DevX.Application;
using DevX.Domain;
using DevX.Cli;
using DevX.Infrastructure.Storage.SQLite;
using DevX.Indexer;
using DevX.Protocol;
using Microsoft.Data.Sqlite;
using IOFile = System.IO.File;

static partial class Program
{
    static async Task<int> SnapshotAsync(string connectionString, string[] args, string format)
    {
        var target = args.Skip(1).FirstOrDefault(argument => !argument.StartsWith("--", StringComparison.Ordinal)
            && argument != GetOption(args, "--session") && argument != GetOption(args, "--agent")
            && argument != GetOption(args, "--task") && argument != GetOption(args, "--format"));
        if (target is null)
        {
            Console.Error.WriteLine("Usage: devx snapshot <file> --session <id>");
            return 1;
        }

        var sessionId = GetOption(args, "--session");
        if (sessionId is null)
        {
            Console.Error.WriteLine("Missing --session.");
            return 1;
        }

        var store = new SqliteStorageInitializer(connectionString);
        var engine = new ProtocolEngine(new SearchService(store, store), store, store);
        var snapshotPayload = new Dictionary<string, string> { ["query"] = target };
        if (format == "agent") snapshotPayload["format"] = format;
        var request = new ProtocolRequest("1.0", ProtocolOperations.GetFileSnapshot, sessionId, JsonSerializer.SerializeToElement(snapshotPayload));
        var started = DateTimeOffset.UtcNow;
        var response = await engine.ExecuteAsync(request);
        await RecordMetricAsync(store, new OperationMetric("snapshot", started, DateTimeOffset.UtcNow - started, response.Success, sessionId, OperationMetadata(response, target), GetAgentLabel(args)));
        WriteResponse(response, format);
        return response.Success ? 0 : 1;
    }

    static async Task<int> MethodAsync(string connectionString, string[] args, string format)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: devx method <summary|get> <method> --session <id>");
            return 1;
        }

        var action = args[1];
        var sessionId = GetOption(args, "--session");
        if (sessionId is null)
        {
            Console.Error.WriteLine("Missing --session.");
            return 1;
        }

        var store = new SqliteStorageInitializer(connectionString);
        await store.InitializeAsync();
        var engine = new ProtocolEngine(new SearchService(store, store), store, store);
        var operation = action == "summary" ? ProtocolOperations.GetMethodSummary : action == "get" ? ProtocolOperations.GetMethod : null;
        if (operation is null)
        {
            Console.Error.WriteLine("Usage: devx method <summary|get> <method> --session <id>");
            return 1;
        }

        var request = new ProtocolRequest("1.0", operation, sessionId, JsonSerializer.SerializeToElement(new { query = args[2] }));
        var started = DateTimeOffset.UtcNow;
        var response = await engine.ExecuteAsync(request);
        await RecordMetricAsync(store, new OperationMetric($"method {action}", started, DateTimeOffset.UtcNow - started, response.Success, sessionId, OperationMetadata(response, args[2]), GetAgentLabel(args)));
        WriteResponse(response, format);
        return response.Success ? 0 : 1;
    }

    static async Task<int> TypeAsync(string connectionString, string[] args, string format)
    {
        if (args.Length < 3 || args[1] != "get")
        {
            Console.Error.WriteLine("Usage: devx type get <type> --session <id>");
            return 1;
        }

        var sessionId = GetOption(args, "--session");
        if (sessionId is null)
        {
            Console.Error.WriteLine("Missing --session.");
            return 1;
        }

        var store = new SqliteStorageInitializer(connectionString);
        await store.InitializeAsync();
        var engine = new ProtocolEngine(new SearchService(store, store), store, store);
        var request = new ProtocolRequest("1.0", ProtocolOperations.GetTypeOperation, sessionId, JsonSerializer.SerializeToElement(new { query = args[2] }));
        var started = DateTimeOffset.UtcNow;
        var response = await engine.ExecuteAsync(request);
        await RecordMetricAsync(store, new OperationMetric("type get", started, DateTimeOffset.UtcNow - started, response.Success, sessionId, OperationMetadata(response, args[2]), GetAgentLabel(args)));
        WriteResponse(response, format);
        return response.Success ? 0 : 1;
    }

    static async Task<int> ReadAsync(string connectionString, string[] args, string format,
        List<(string Target, ProtocolResponse Response)>? batchResponses = null)
    {
        var positional = args.Skip(1).TakeWhile(argument => !argument.StartsWith("--", StringComparison.Ordinal)).ToArray();
        var inferredKind = positional.Length == 1;
        if (args.Length > 1 && args[1] == "method")
        {
            if (!TryParseExplicitMethodRead(args, out var methodTargets, out var methodOptions, out var parseError))
            {
                Console.Error.WriteLine($"invalid read method request: {parseError}");
                return 2;
            }

            var hasId = GetOption(args, "--id") is not null;
            var hasQuery = GetOption(args, "--query") is not null;
            if (hasId && hasQuery)
            {
                Console.Error.WriteLine("invalid read method request: use either --id or --query, not both");
                return 2;
            }

            if ((hasId || hasQuery) && methodTargets.Length > 0)
            {
                Console.Error.WriteLine("invalid read method request: --id/--query cannot be combined with positional targets");
                return 2;
            }

            if (methodTargets.Length == 0 && !hasId && !hasQuery)
            {
                Console.Error.WriteLine("invalid read method request: provide a target or use --id/--query");
                return 2;
            }

            if (methodTargets.Length > 10)
            {
                Console.Error.WriteLine("invalid read method request: at most 10 targets are allowed");
                return 2;
            }

            if (methodTargets.Length > 1)
            {
                if (string.IsNullOrWhiteSpace(GetOption(args, "--session")))
                {
                    Console.Error.WriteLine("invalid read method batch: --session is required");
                    return 2;
                }

                var results = new List<(string Target, ProtocolResponse Response)>();
                foreach (var target in methodTargets)
                {
                    var singleArgs = new[] { "read", "method", target }.Concat(methodOptions).ToArray();
                    await ReadAsync(connectionString, singleArgs, format, results);
                }

                if (format == "json")
                {
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        results = results.Select(item => new
                        {
                            target = item.Target,
                            success = item.Response.Success,
                            data = item.Response.Data,
                            errors = item.Response.Errors
                        })
                    }));
                }
                else
                {
                    foreach (var item in results)
                    {
                        Console.WriteLine($"=== {item.Target} ===");
                        if (!item.Response.Success)
                        {
                            Console.WriteLine($"error [{item.Target}]: {string.Join("; ", item.Response.Errors.Select(error => error.Message))}");
                        }
                        else if (format == "agent")
                        {
                            WriteResponse(item.Response, format);
                        }
                        else
                        {
                            WriteSourceResponse(item.Response, HasFlag(methodOptions, "--context"));
                        }
                    }
                }

                return results.Any(item => !item.Response.Success) ? 2 : 0;
            }
        }

        if (positional.Length > 1 && positional[0] is not ("file" or "method" or "type" or "section"))
        {
            var failed = false;
            foreach (var target in positional)
            {
                Console.WriteLine($"=== {target} ===");
                var one = await ReadAsync(connectionString, ["read", target, .. args.Skip(1 + positional.Length)], format);
                if (one != 0) failed = true;
            }
            return failed ? 2 : 0;
        }
        if (positional.Length == 1)
        {
            var target = positional[0];
            var kind = target.Contains('#') ? "section" :
                ((target.Contains(Path.DirectorySeparatorChar) || Path.HasExtension(target)) && IOFile.Exists(target) ? "file" : "method");
            var remainder = args.Skip(2).ToArray();
            args = ["read", kind, target, .. remainder];
            if (kind == "file" && !HasFlag(args, "--full") && GetOption(args, "--range") is null) args = [.. args, "--full"];
            positional = [kind, target];
        }
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: devx read <name|file#Heading|file> [<name> ...] [--session <id>] | devx read <file|method|type|section> <target> [--range start:end|--full]. target is a qualified name (e.g. sympy.core.symbol.Symbol) or a repo-relative path for kind=file; never a path for kind=method/type. A search hit already shows the class signature and bases; use read for bodies; read type (outline) also shows a bases: line with each base's path -- follow a hierarchy one read type per level, no search between; it answers 'which methods/fields does X have' -- use --full only when you need bodies. callers/references work for Python too (text-backed); callees does not -- read the body instead. Bare or Class.member names resolve when unique: do not search first when you know the name.");
            return 1;
        }

        var action = args[1];
        var sessionId = GetOption(args, "--session");
        if (sessionId is null)
        {
            Console.Error.WriteLine("Missing --session.");
            return 1;
        }

        var store = new SqliteStorageInitializer(connectionString);
        await store.InitializeAsync();
        var engine = new ProtocolEngine(new SearchService(store, store), store, store);
        ProtocolRequest request;
        var started = DateTimeOffset.UtcNow;
        var readTarget = args[2];
        if (action is "method" or "type" && GetOption(args, "--id") is null && GetOption(args, "--query") is null && !readTarget.Contains('.'))
        {
            var lookup = await engine.ExecuteAsync(new ProtocolRequest("1.0", ProtocolOperations.SearchSymbol, sessionId, JsonSerializer.SerializeToElement(new { query = readTarget })));
            if (lookup.Data is { ValueKind: JsonValueKind.Object } result && TryGetPropertyIgnoreCase(result, "Symbols", out var symbols) && symbols.ValueKind == JsonValueKind.Array)
            {
                var exact = symbols.EnumerateArray().Where(symbol => TryGetPropertyIgnoreCase(symbol, "Name", out var name) && name.GetString() == readTarget).ToArray();
                if (exact.Length == 1)
                {
                    var symbolKind = JsonPropertyText(exact[0], "Kind");
                    if (inferredKind && symbolKind is "Class" or "Type" or "Record" or "Interface" or "Struct" or "Enum") action = "type";
                }
            }
        }

        if (action == "file")
        {
            var full = HasFlag(args, "--full");
            var range = GetOption(args, "--range");
            if (!full && range is null)
            {
                Console.Error.WriteLine("Usage: devx read file <file> --range start:end --session <id> | --full");
                return 1;
            }

            request = new ProtocolRequest("1.0", "READ_FILE", sessionId, JsonSerializer.SerializeToElement(new { query = readTarget, range, full }), GetAgentLabel(args));
        }
        else if (action == "method")
        {
            var id = GetOption(args, "--id");
            var query = GetOption(args, "--query");
            if (id is null && query is null)
            {
                query = readTarget;
            }

            var methodPayload = new Dictionary<string, object?>();
            if (id is not null) methodPayload["id"] = id;
            else methodPayload["query"] = query;
            if (HasFlag(args, "--context")) methodPayload["context"] = true;
            request = new ProtocolRequest("1.0", "READ_METHOD", sessionId, JsonSerializer.SerializeToElement(methodPayload), GetAgentLabel(args));
        }
        else if (action == "type")
        {
            var id = GetOption(args, "--id");
            var query = GetOption(args, "--query");
            if (id is null && query is null)
            {
                query = readTarget;
            }

            request = id is not null
                ? new ProtocolRequest("1.0", "READ_TYPE", sessionId, JsonSerializer.SerializeToElement(new { id, full = HasFlag(args, "--full") }), GetAgentLabel(args))
                : new ProtocolRequest("1.0", "READ_TYPE", sessionId, JsonSerializer.SerializeToElement(new { query, full = HasFlag(args, "--full") }), GetAgentLabel(args));
        }
        else if (action == "section")
        {
            var separator = SectionTarget.SeparatorIndex(args[2]);
            if (separator <= 0)
            {
                // 19 of 23 `read section` calls in the v2 arm-B transcripts failed on exactly
                // this -- a missing '#Heading'. Answer with the headings rather than an error,
                // so the retry is a paste instead of another search. A trailing '#' is not this
                // case: it names nothing, and the engine answers it with the file itself.
                var snapshot = await engine.ExecuteAsync(new ProtocolRequest("1.0", ProtocolOperations.GetFileSnapshot, sessionId, JsonSerializer.SerializeToElement(new { query = args[2] }), GetAgentLabel(args)));
                Console.Error.WriteLine("devx read section needs a heading: devx read section <file>#<heading>");
                var headings = new List<string>();
                var payload = snapshot.Data;
                // The snapshot payload nests its collections under Data.Snapshot.
                if (payload is { ValueKind: JsonValueKind.Object } outer && TryGetPropertyIgnoreCase(outer, "Snapshot", out var inner))
                    payload = inner;
                if (snapshot.Success && payload is { ValueKind: JsonValueKind.Object } snapData)
                    foreach (var collection in new[] { "Types", "Methods" })
                        if (TryGetPropertyIgnoreCase(snapData, collection, out var items) && items.ValueKind == JsonValueKind.Array)
                            headings.AddRange(items.EnumerateArray().Select(item => JsonPropertyText(item, "Name")).Where(name => name.Length > 0));
                if (headings.Count == 0) Console.Error.WriteLine($"no headings found in '{args[2]}' (is it indexed?)");
                else
                {
                    Console.Error.WriteLine($"headings in {args[2]}:");
                    foreach (var heading in headings) Console.Error.WriteLine($"  devx read section \"{args[2]}#{heading}\"");
                }
                return 1;
            }

            request = new ProtocolRequest("1.0", "READ_SECTION", sessionId, JsonSerializer.SerializeToElement(new
            {
                file = args[2][..separator],
                heading = args[2][(separator + 1)..]
            }), GetAgentLabel(args));
        }
        else
        {
            Console.Error.WriteLine("Usage: devx read <name|file#Heading|file> [<name> ...] [--session <id>] | devx read <file|method|type|section> <target> [--range start:end|--full]. target is a qualified name (e.g. sympy.core.symbol.Symbol) or a repo-relative path for kind=file; never a path for kind=method/type. A search hit already shows the class signature and bases; use read for bodies; read type (outline) also shows a bases: line with each base's path -- follow a hierarchy one read type per level, no search between; it answers 'which methods/fields does X have' -- use --full only when you need bodies. callers/references work for Python too (text-backed); callees does not -- read the body instead. Bare or Class.member names resolve when unique: do not search first when you know the name.");
            return 1;
        }

        var response = await engine.ExecuteAsync(request);
        await RecordMetricAsync(store, new OperationMetric($"read {action}", started, DateTimeOffset.UtcNow - started, response.Success, sessionId, OperationMetadata(response, args[2]), GetAgentLabel(args)));
        if (batchResponses is not null)
        {
            batchResponses.Add((args[2], response));
            return response.Success ? 0 : 1;
        }
        var ambiguousError = response.Success || format == "json" ? null : response.Errors.FirstOrDefault(error => error.Code == "AmbiguousMatch" && error.Candidates is { Count: > 0 });
        if (ambiguousError is not null)
        {
            Console.WriteLine(FormatAmbiguousCandidates(ambiguousError, args[2]));
            return 2;
        }
        if (format is "json" or "agent" || action == "type")
        {
            WriteResponse(response, format);
        }
        else
        {
            WriteSourceResponse(response, HasFlag(args, "--context"));
        }
        return response.Success ? 0 : 1;
    }

    static string FormatAmbiguousCandidates(ProtocolError error, string target)
    {
        var total = error.TotalCandidates ?? error.Candidates!.Count;
        var lines = new List<string> { $"ambiguous: {target} matches {total} symbols" };
        foreach (var candidate in error.Candidates!.Take(10))
            lines.Add($"{candidate.QualifiedName}  {RelativeAgentPath(candidate.FilePath ?? string.Empty)}:{candidate.StartLine}-{candidate.EndLine}  {candidate.Kind}");
        lines.Add($"TotalCandidates: {total}" + (total > 10 ? " (showing 10)" : string.Empty));
        lines.Add("Symbol matched multiple definitions; pass a qualified name from the list above.");
        return string.Join("\n", lines);
    }

    static bool TryParseExplicitMethodRead(string[] args, out string[] targets, out string[] options, out string? error)
    {
        var parsedTargets = new List<string>();
        var parsedOptions = new List<string>();
        var valueOptions = new HashSet<string>(StringComparer.Ordinal)
        {
            "--id", "--query", "--session", "--agent", "--format", "--task", "--range"
        };
        var flagOptions = new HashSet<string>(StringComparer.Ordinal) { "--context", "--full" };

        for (var index = 2; index < args.Length; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                parsedTargets.Add(argument);
                continue;
            }

            if (flagOptions.Contains(argument))
            {
                parsedOptions.Add(argument);
                continue;
            }

            if (!valueOptions.Contains(argument))
            {
                targets = [];
                options = [];
                error = $"unrecognized option or extra argument '{argument}'";
                return false;
            }

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                targets = [];
                options = [];
                error = $"option '{argument}' requires a value";
                return false;
            }

            parsedOptions.Add(argument);
            parsedOptions.Add(args[++index]);
        }

        targets = parsedTargets.ToArray();
        options = parsedOptions.ToArray();
        error = null;
        return true;
    }
}
