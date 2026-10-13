using System.Text.Json;
using DevX.Application;
using DevX.Domain;
using DevX.Cli;
using DevX.Infrastructure.Storage.SQLite;
using DevX.Indexer;
using DevX.Protocol;

static partial class Program
{
    static string? OperationMetadata(ProtocolResponse response, string? attemptedName = null)
    {
        var responseCharacters = response.Data?.GetRawText().Length ?? response.Errors.Sum(error => error.Code.Length + error.Message.Length);
        var estimatedTokens = (int)Math.Ceiling(responseCharacters / 4d);
        string? summary = null;
        if (response.Data is { ValueKind: JsonValueKind.Object } data
            && data.TryGetProperty("Execution", out var execution)
            && execution.ValueKind == JsonValueKind.Object
            && execution.TryGetProperty("Summary", out var summaryElement)
            && summaryElement.ValueKind == JsonValueKind.String)
        {
            summary = summaryElement.GetString();
        }

        if (response.Success)
        {
            string? resolvedVia = null;
            long? wholeFileCharacters = null;
            long? returnedCharacters = null;
            string? readEventId = null;
            if (response.Data is { ValueKind: JsonValueKind.Object } source && source.TryGetProperty("Metadata", out var metadata)
                && metadata.ValueKind == JsonValueKind.Object)
            {
                if (metadata.TryGetProperty("ResolvedVia", out var via)) resolvedVia = via.GetString();
                if (metadata.TryGetProperty("WholeFileCharacters", out var whole)) wholeFileCharacters = whole.GetInt64();
                if (metadata.TryGetProperty("ReturnedCharacters", out var returned)) returnedCharacters = returned.GetInt64();
                if (metadata.TryGetProperty("ReadEventId", out var readId)) readEventId = readId.GetString();
            }
            return JsonSerializer.Serialize(new
            {
                AttemptedName = attemptedName,
                Summary = summary,
                ResolvedVia = resolvedVia,
                WholeFileCharacters = wholeFileCharacters,
                ReturnedCharacters = returnedCharacters,
                ReadEventId = readEventId,
                ResponseCharacters = responseCharacters,
                EstimatedTokens = estimatedTokens
            });
        }

        var error = response.Errors.FirstOrDefault();
        return JsonSerializer.Serialize(new
        {
            ErrorCode = error?.Code ?? "UnknownFailure",
            FailureReason = error?.Message ?? "The operation failed without a protocol error.",
            AttemptedName = error?.AttemptedName ?? attemptedName,
            Suggestions = error?.Suggestions ?? [],
            Candidates = error?.Candidates,
            Summary = summary,
            ResponseCharacters = responseCharacters,
            EstimatedTokens = estimatedTokens
        });
    }

    static void WriteResponse(ProtocolResponse response, string format,
        IReadOnlyDictionary<(string FileId, int Line), string>? enclosingSymbols = null)
    {
        if (format == "json")
        {
            Console.WriteLine(JsonSerializer.Serialize(response));
            return;
        }

        if (format == "agent")
        {
            Console.WriteLine(FormatAgentResponse(response, enclosingSymbols));
            return;
        }

        if (response.Success)
        {
            Console.WriteLine(response.Data?.GetRawText() ?? "{}");
            return;
        }

        foreach (var error in response.Errors)
        {
            Console.Error.WriteLine($"{error.Code}: {error.Message}");
        }
    }

    static string FormatAgentResponse(ProtocolResponse response,
        IReadOnlyDictionary<(string FileId, int Line), string>? enclosingSymbols = null)
    {
        if (!response.Success) return string.Join("\n", response.Errors.Select(error => $"error: {error.Message}"));
        if (response.Data is not { } data) return string.Empty;
        if (TryGetPropertyIgnoreCase(data, "symbols", out var symbols) && symbols.ValueKind == JsonValueKind.Array && symbols.GetArrayLength() > 0)
        {
            var hits = symbols.EnumerateArray().Take(10).Select(item => $"{JsonPropertyText(item, "Kind").ToLowerInvariant()} {JsonPropertyText(item, "QualifiedName")} {RelativeAgentPath(JsonPropertyText(item, "FilePath"))}:{JsonPropertyText(item, "StartLine")}-{JsonPropertyText(item, "EndLine")}").ToList();
            if (symbols.GetArrayLength() > hits.Count) hits.Add($"... {symbols.GetArrayLength() - hits.Count} more");
            return string.Join("\n", hits);
        }
        if (TryGetPropertyIgnoreCase(data, "contentMatches", out var matches) && matches.ValueKind == JsonValueKind.Array)
        {
            var hits = matches.EnumerateArray().Take(10).Select(item =>
            {
                var preview = JsonPropertyText(item, "Preview").Trim();
                var line = JsonPropertyText(item, "Line");
                var symbol = enclosingSymbols is not null
                    && enclosingSymbols.TryGetValue((JsonPropertyText(item, "FileId"), int.TryParse(line, out var at) ? at : 0), out var name)
                        ? $" [{name}]"
                        : string.Empty;
                return $"{RelativeAgentPath(JsonPropertyText(item, "Path"))}:{line}: {preview[..Math.Min(120, preview.Length)]}{symbol}";
            }).ToList();
            if (matches.GetArrayLength() > hits.Count)
            {
                // The cap stays; the message now says what to do about it. "N more" alone told the
                // agent nothing, which is why the next call was usually the same search reworded.
                hits.Add($"... {matches.GetArrayLength() - hits.Count} more: narrow with a second word, "
                         + "alternation (a|b), or several queries in one call, e.g. devx search text \"a\" \"b\"");
            }

            var coverageNote = ContentSearchCoverageFormatter.Format(data);
            if (coverageNote is not null) hits.Add(coverageNote);

            return string.Join("\n", hits);
        }
        if (TextReferences.Format(data) is { } textBacked) return textBacked;
        foreach (var collection in new[] { "Callers", "Callees", "References" })
        {
            if (!TryGetPropertyIgnoreCase(data, collection, out var items)) continue;
            if (items.ValueKind == JsonValueKind.Object && TryGetPropertyIgnoreCase(items, collection, out var nested)) items = nested;
            if (items.ValueKind == JsonValueKind.Array)
                return string.Join("\n", items.EnumerateArray().Select(item => $"{RelativeAgentPath(JsonPropertyText(item, "FilePath"))}:{JsonPropertyText(item, collection == "References" ? "LineNumber" : "CallSiteLine")}: {JsonPropertyText(item, "QualifiedName")}"));
        }
        if (TryGetPropertyIgnoreCase(data, "snapshot", out var snapshot))
        {
            var lines = new List<string> { $"{RelativeAgentPath(JsonPropertyText(snapshot, "Path"))} ({JsonPropertyText(snapshot, "Language")}, {JsonPropertyText(snapshot, "ApproximateLoc")} lines)" };
            if (TryGetPropertyIgnoreCase(snapshot, "types", out var types) && types.ValueKind == JsonValueKind.Array)
                lines.AddRange(types.EnumerateArray().Where(type => JsonPropertyText(type, "StartLine").Length > 0 && JsonPropertyText(type, "EndLine").Length > 0).Select(type => $"type {JsonPropertyText(type, "Name")} :{JsonPropertyText(type, "StartLine")}-{JsonPropertyText(type, "EndLine")}"));
            if (TryGetPropertyIgnoreCase(snapshot, "methods", out var methods) && methods.ValueKind == JsonValueKind.Array)
                lines.AddRange(methods.EnumerateArray().Where(method => JsonPropertyText(method, "StartLine").Length > 0 && JsonPropertyText(method, "EndLine").Length > 0).Select(method => $"  method {JsonPropertyText(method, "Name")}({AgentSignature(JsonPropertyText(method, "Signature"))}) :{JsonPropertyText(method, "StartLine")}-{JsonPropertyText(method, "EndLine")}"));
            return string.Join("\n", lines);
        }
        if (TryGetPropertyIgnoreCase(data, "path", out var path) && TryGetPropertyIgnoreCase(data, "source", out var source))
        {
            var p = RelativeAgentPath(path.GetString() ?? "");
            int.TryParse(JsonPropertyText(data, "StartLine"), out var start); int.TryParse(JsonPropertyText(data, "EndLine"), out var end);
            var name = JsonPropertyText(data, "QualifiedName");
            var kind = JsonPropertyText(data, "Kind");
            // A `read section <file>#<name>` asked for a heading and got a symbol; say which one,
            // so the caller can tell a resolution from a literal match.
            var resolved = string.Empty;
            if (TryGetPropertyIgnoreCase(data, "Metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object
                && JsonPropertyText(metadata, "ResolvedVia") == "section-symbol")
            {
                var note = JsonPropertyText(metadata, "Message");
                if (note.Length > 0) resolved = note + "\n";
            }
            var context = TryGetPropertyIgnoreCase(data, "Metadata", out metadata) ? FormatReadContext(metadata) : string.Empty;
            var cappedNote = !string.IsNullOrEmpty(kind) && TryGetPropertyIgnoreCase(data, "Truncated", out var truncatedFlag) && truncatedFlag.ValueKind == JsonValueKind.True
                ? $" (truncated: {JsonPropertyText(data, "ReturnedLines")} of {end - start + 1} lines shown; pass --full for the rest)" : string.Empty;
            return $"{resolved}{p}:{start}-{end}{(string.IsNullOrEmpty(name) ? "" : $" {name}")}{(string.IsNullOrEmpty(kind) ? "" : $" [{kind}]")}{cappedNote}\n{source.GetString()}{context}";
        }
        return data.GetRawText();
    }

    // The source an auto-read appends to a single-hit search result. An EXPLICIT read always
    // returns its source (the 1.6.1 paging stands); this cap is only for source the agent did
    // not ask for, so an unlucky one-hit search cannot dump a 900-line method into the context.
    static string AutoReadText(ProtocolResponse read)
    {
        const int AutoReadMaxLines = 80;
        var text = FormatAgentResponse(read);
        if (read.Data is not { ValueKind: JsonValueKind.Object } data) return text;
        var head = text.Split('\n')[0];
        int.TryParse(JsonPropertyText(data, "StartLine"), out var start);
        int.TryParse(JsonPropertyText(data, "EndLine"), out var end);
        var span = end >= start && start > 0 ? end - start + 1 : 0;
        if (span <= AutoReadMaxLines) return text;
        var body = text.Contains('\n') ? text[(text.IndexOf('\n') + 1)..] : string.Empty;
        var first = body.Split('\n').FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim() ?? string.Empty;
        return $"{head} {first}\nread to get {span} lines";
    }

    static string RelativeAgentPath(string path) => string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) ? path : Path.GetRelativePath(Environment.CurrentDirectory, path);

    static string AgentSignature(string signature)
    {
        var start = signature.IndexOf('(');
        var end = signature.IndexOf(')', start + 1);
        return start >= 0 && end > start ? signature[(start + 1)..end] : signature;
    }

    static void WriteSourceResponse(ProtocolResponse response, bool showContext = false)
    {
        if (!response.Success || response.Data is null)
        {
            foreach (var error in response.Errors) Console.Error.WriteLine($"{error.Code}: {error.Message}");
            return;
        }

        var source = response.Data.Value.Deserialize<SourceReadResponse>();
        if (source is null)
        {
            Console.WriteLine(response.Data.Value.GetRawText());
            return;
        }

        // A section request that named a symbol rather than a heading resolves to it; print what
        // it resolved to, so the reader is not left guessing why the span is not the whole file.
        if (source.Metadata is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Object } metadata
            && metadata.TryGetProperty("ResolvedVia", out var resolvedVia) && resolvedVia.GetString() == "section-symbol"
            && metadata.TryGetProperty("Message", out var resolvedMessage) && resolvedMessage.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            Console.WriteLine(resolvedMessage.GetString());
        }
        var location = source.StartLine == source.EndLine
            ? $"{source.Path}:{source.StartLine}"
            : $"{source.Path}:{source.StartLine}-{source.EndLine}";
        Console.WriteLine(location);
        if (source.SourceReturned && source.Source.Length > 0) Console.WriteLine(source.Source);
        if (showContext && source.Metadata is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Object } contextMetadata)
        {
            Console.Write(FormatReadContext(contextMetadata));
        }
    }

    static string FormatReadContext(JsonElement metadata)
    {
        if (!TryGetPropertyIgnoreCase(metadata, "Context", out var context)) return string.Empty;
        var candidatesFound = false;
        var lines = new List<string>();
        foreach (var groupName in new[] { "TestNameMatches", "SameFileCandidates" })
        {
            if (!TryGetPropertyIgnoreCase(context, groupName, out var candidates) || candidates.ValueKind != JsonValueKind.Array) continue;
            foreach (var candidate in candidates.EnumerateArray())
            {
                if (!candidatesFound) lines.Add("navigation candidates (incomplete; name/path evidence only):");
                candidatesFound = true;
                var relation = TryGetPropertyIgnoreCase(candidate, "Relation", out var relationValue) ? relationValue.GetString() : "candidate";
                var qualifiedName = TryGetPropertyIgnoreCase(candidate, "QualifiedName", out var nameValue) ? nameValue.GetString() : "";
                var path = TryGetPropertyIgnoreCase(candidate, "FilePath", out var pathValue) ? pathValue.GetString() : "";
                var line = TryGetPropertyIgnoreCase(candidate, "StartLine", out var lineValue) && lineValue.ValueKind == JsonValueKind.Number
                    ? $":{lineValue.GetInt32()}" : "";
                var symbolId = TryGetPropertyIgnoreCase(candidate, "SymbolId", out var idValue) ? idValue.GetString() : "";
                lines.Add($"  {relation}: {qualifiedName}  {path}{line}  id={symbolId}");
            }
        }
        if (!candidatesFound) lines.Add("no supported navigation candidates");
        if (TryGetPropertyIgnoreCase(context, "TestNameMatchesTruncated", out var testsTruncated) && testsTruncated.ValueKind == JsonValueKind.True) lines.Add("  +more test candidates");
        if (TryGetPropertyIgnoreCase(context, "SameFileCandidatesTruncated", out var definitionsTruncated) && definitionsTruncated.ValueKind == JsonValueKind.True) lines.Add("  +more same-file candidates");
        if (TryGetPropertyIgnoreCase(context, "SourceNamesTruncated", out var sourceNamesTruncated) && sourceNamesTruncated.ValueKind == JsonValueKind.True) lines.Add("  source name scan truncated");
        return "\n" + string.Join("\n", lines);
    }
}
