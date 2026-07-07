using System.Text;
using System.Text.Json;
using Ecp.Domain;
using Ecp.Protocol;

namespace Ecp.Cli;

public sealed record AskContextWorkflowSnapshot(string Path, IReadOnlyList<string> Lines, IReadOnlyList<string> Methods, IReadOnlyList<string> Symbols, string Rendered);
public sealed record AskContextWorkflowContextRequest(string Type, string Path, string Target, string Reason);
public sealed record AskContextSourceBlock(string Path, string Range, string Source);
public sealed record AskContextWorkflowReadyForEdit(string Understanding, IReadOnlyList<(string Quote, string Meaning)> Evidence, IReadOnlyList<string> TargetFiles, IReadOnlyList<string> TargetRanges);
public sealed record AskContextWorkflowResult(AskContextWorkflowReadyForEdit? ReadyForEdit, string? EditProposal, bool Success, string? Summary, string? FailureReason, IReadOnlyList<AskContextSourceBlock>? SourceBlocks = null);
public sealed record AskContextWorkflowCompletion(string Summary, string? EditProposal);

public sealed class AskContextWorkflow
{
    private readonly ILlmClient _llm;
    private readonly IProtocolEngine _protocolEngine;
    private readonly bool _debugAsk;
    private const int MaxContextRounds = 5;

    public AskContextWorkflow(ILlmClient llm, IProtocolEngine protocolEngine, bool debugAsk = false)
    {
        _llm = llm;
        _protocolEngine = protocolEngine;
        _debugAsk = debugAsk;
    }

    public async Task<AskContextWorkflowResult> RunAsync(
        string userGoal,
        AskRepoMap repoMap,
        SessionState session,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine("phase=Snapshot");
        var snapshot = await ReadSnapshotAsync("src/Ecp.Cli/Program.cs", cancellationToken);
        Console.WriteLine($"snapshot_file={snapshot.Path}");
        Console.WriteLine($"snapshot_methods={snapshot.Methods.Count}");
        Console.WriteLine($"snapshot_symbols={snapshot.Symbols.Count}");
        Console.WriteLine("snapshot begin");
        Console.WriteLine(snapshot.Rendered);
        Console.WriteLine("snapshot end");

        var readContext = string.Empty;
        var providedSnippet = string.Empty;
        var sawContextBlock = false;
        var executedRequests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceBlocks = new List<AskContextSourceBlock>();
        var contextRounds = 0;
        while (true)
        {
            if (contextRounds++ >= MaxContextRounds)
            {
                Console.WriteLine("invalid response begin");
                Console.WriteLine("Max context rounds reached.");
                Console.WriteLine("invalid response end");
                return new AskContextWorkflowResult(null, null, false, "Context workflow failed.", "max_context_rounds", sourceBlocks);
            }

            var prompt = BuildContextPrompt(userGoal, repoMap, snapshot, readContext);
            var response = await _llm.CompleteAsync(prompt, cancellationToken);
            if (_debugAsk)
            {
                Console.WriteLine("context response raw begin");
                Console.WriteLine(response);
                Console.WriteLine("context response raw end");
            }

            if (string.IsNullOrWhiteSpace(response))
            {
                Console.WriteLine("invalid context response reason=empty_output");
                Console.WriteLine("invalid response begin");
                Console.WriteLine("invalid response end");
                return new AskContextWorkflowResult(null, null, false, "Context workflow failed.", "invalid_context_response:empty_output", sourceBlocks);
            }

            var contextRequests = ParseContextRequests(response);
            if (contextRequests.Count > 0)
            {
                if (ContainsEntireFileRangeRequest(contextRequests))
                {
                    var repairPrompt = BuildRangeTooBroadRepairPrompt(userGoal, repoMap, snapshot, contextRequests);
                    Console.WriteLine("context_failure_reason=range_too_broad");
                    Console.WriteLine("range_too_broad repair prompt begin");
                    Console.WriteLine(repairPrompt);
                    Console.WriteLine("range_too_broad repair prompt end");
                    readContext = repairPrompt;
                    continue;
                }

                var execution = await ExecuteContextRequestsAsync(snapshot, contextRequests, executedRequests, cancellationToken);
                readContext = execution.Context;
                sourceBlocks.AddRange(execution.SourceBlocks);
                sawContextBlock = sawContextBlock || !string.IsNullOrWhiteSpace(execution.ProvidedContextSource);
                if (execution.SourceBlocks.Count > 0)
                {
                    if (_debugAsk)
                    {
                        Console.WriteLine("context_complete_reason=source_blocks_captured");
                    }

                    return new AskContextWorkflowResult(null, null, true, "Context workflow complete; model received requested source context.", null, sourceBlocks);
                }

                if (execution.RepeatedSameContextRequest)
                {
                    if (sourceBlocks.Count > 0)
                    {
                        return new AskContextWorkflowResult(null, null, true, "Context workflow complete; model received requested source context.", "duplicate_context_complete", sourceBlocks);
                    }

                    return new AskContextWorkflowResult(null, null, false, "Context workflow failed.", "no_source_blocks", sourceBlocks);
                }

                if (execution.DuplicateContextRequest)
                {
                    readContext = BuildPostContextPrompt(userGoal, repoMap, snapshot, contextRequests, execution.ProvidedContextSource);
                    Console.WriteLine("post-context prompt begin");
                    Console.WriteLine(readContext);
                    Console.WriteLine("post-context prompt end");
                }

                continue;
            }

            JsonDocument? parsedDocument = null;
            try
            {
                parsedDocument = JsonDocument.Parse(ExtractJsonPayload(response));
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"invalid context response reason=parse_failure:{ex.Message}");
                Console.WriteLine("invalid response begin");
                Console.WriteLine(response);
                Console.WriteLine("invalid response end");
                return new AskContextWorkflowResult(null, null, false, "Context workflow failed.", $"parse_failure:{ex.Message}", sourceBlocks);
            }

            using (parsedDocument)
            {
                if (TryGetKind(parsedDocument.RootElement) is "change_proposal")
                {
                    if (sawContextBlock)
                    {
                        return new AskContextWorkflowResult(null, response, true, "Context workflow complete; model produced an edit proposal.", null, sourceBlocks);
                    }

                    var correctionPrompt = BuildChangeProposalCorrectionPrompt(userGoal, repoMap, snapshot, readContext, readContext);
                    Console.WriteLine("change_proposal correction prompt begin");
                    Console.WriteLine(correctionPrompt);
                    Console.WriteLine("change_proposal correction prompt end");
                    readContext = correctionPrompt;
                    continue;
                }

                var readyForEdit = ParseReadyForEdit(response);
                if (readyForEdit is not null)
                {
                    return new AskContextWorkflowResult(readyForEdit, null, true, "Context workflow complete; ready for edit.", null, sourceBlocks);
                }

                Console.WriteLine("invalid context response reason=wrong_schema");
                Console.WriteLine("invalid response begin");
                Console.WriteLine(response);
                Console.WriteLine("invalid response end");
                return new AskContextWorkflowResult(null, null, false, "Context workflow failed.", "invalid_context_response:wrong_schema", sourceBlocks);
            }
        }
    }

    private async Task<AskContextWorkflowSnapshot> ReadSnapshotAsync(string path, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        var methods = new List<string>();
        var symbols = new List<string>();
        var rendered = new StringBuilder();

        var fullPath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, path));
        if (System.IO.File.Exists(fullPath))
        {
            var source = await System.IO.File.ReadAllTextAsync(fullPath, cancellationToken);
            var sourceLines = source.Split('\n');
            for (var index = 0; index < sourceLines.Length; index++)
            {
                var lineNumber = index + 1;
                var line = sourceLines[index];
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var trimmed = line.Trim();
                if (!ShouldIncludeSnapshotLine(trimmed))
                {
                    continue;
                }

                lines.Add($"{lineNumber}: {line}");
                rendered.AppendLine($"{lineNumber}: {line}");

                if (trimmed.Contains("switch", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("case", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("PrintHelp", StringComparison.OrdinalIgnoreCase))
                {
                    symbols.Add($"{lineNumber}: {trimmed}");
                }
                if (trimmed.Contains("static", StringComparison.OrdinalIgnoreCase) && trimmed.Contains("(", StringComparison.OrdinalIgnoreCase) && trimmed.Contains(")", StringComparison.OrdinalIgnoreCase))
                {
                    methods.Add($"{lineNumber}: {trimmed}");
                }
            }
        }

        return new AskContextWorkflowSnapshot(path, lines, methods, symbols, rendered.ToString());
    }

    private static bool ShouldIncludeSnapshotLine(string line)
    {
        return line.Contains("static", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Task<int>", StringComparison.OrdinalIgnoreCase)
            || line.Contains("switch", StringComparison.OrdinalIgnoreCase)
            || line.Contains("case", StringComparison.OrdinalIgnoreCase)
            || line.Contains("PrintHelp", StringComparison.OrdinalIgnoreCase)
            || line.Contains("AskAsync", StringComparison.OrdinalIgnoreCase)
            || line.Contains("RunAsync", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildContextPrompt(string userGoal, AskRepoMap repoMap, AskContextWorkflowSnapshot snapshot, string readContext)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are the ASK context workflow.");
        builder.AppendLine("User goal:");
        builder.AppendLine(userGoal);
        builder.AppendLine("Repo map:");
        builder.AppendLine(repoMap.RenderedMap);
        builder.AppendLine("Snapshot:");
        builder.AppendLine(snapshot.Rendered);
        if (!string.IsNullOrWhiteSpace(readContext))
        {
            builder.AppendLine("Read context:");
            builder.AppendLine(readContext);
        }
        builder.AppendLine("Do not write code yet. Tell me exactly which method(s), line ranges, or regions you need to read next.");
        builder.AppendLine("Allowed response schemas:");
        builder.AppendLine("{ \"neededContext\": [ { \"type\": \"read_range|read_method|read_type|search_symbol|search_text|snapshot\", \"path\": \"...\", \"target\": \"...\", \"reason\": \"...\" } ] }");
        builder.AppendLine("{ \"kind\": \"context_request\", \"neededContext\": [ { \"type\": \"read_range|read_method|read_type|search_symbol|search_text|snapshot\", \"path\": \"...\", \"target\": \"...\", \"reason\": \"...\" } ] }");
        builder.AppendLine("{ \"kind\": \"ready_for_edit\", \"understanding\": \"...\", \"evidence\": [ { \"quote\": \"...\", \"meaning\": \"...\" } ], \"targetFiles\": [], \"targetRanges\": [] }");
        return builder.ToString();
    }

    private static IReadOnlyList<AskContextWorkflowContextRequest> ParseContextRequests(string response)
    {
        JsonDocument? document;
        try
        {
            document = JsonDocument.Parse(ExtractJsonPayload(response));
        }
        catch (JsonException)
        {
            return [];
        }

        using var parsedDocument = document;
        var root = document.RootElement;
        if (TryGetKind(root) is not null and not "context_request")
        {
            return [];
        }

        if (!root.TryGetProperty("neededContext", out var neededContextProp) || neededContextProp.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var requests = new List<AskContextWorkflowContextRequest>();
        foreach (var item in neededContextProp.EnumerateArray())
        {
            var type = item.TryGetProperty("type", out var typeProp) ? typeProp.GetString() ?? string.Empty : string.Empty;
            var path = item.TryGetProperty("path", out var pathProp) ? pathProp.GetString() ?? string.Empty : string.Empty;
            var target = item.TryGetProperty("target", out var targetProp) ? targetProp.GetString() ?? string.Empty : string.Empty;
            var reason = item.TryGetProperty("reason", out var reasonProp) ? reasonProp.GetString() ?? string.Empty : string.Empty;
            if (!string.IsNullOrWhiteSpace(type) && !string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(target))
            {
                requests.Add(new AskContextWorkflowContextRequest(type, path, target, reason));
            }
        }

        return requests;
    }

    private static string ExtractJsonPayload(string response)
    {
        var trimmed = response.Trim();
        var startIndex = trimmed.IndexOf('{');
        var endIndex = trimmed.LastIndexOf('}');
        if (startIndex >= 0 && endIndex > startIndex)
        {
            return trimmed[startIndex..(endIndex + 1)];
        }

        return trimmed;
    }

    private sealed record ContextExecutionResult(string Context, bool DuplicateContextRequest, bool RepeatedSameContextRequest, string ProvidedContextSource, IReadOnlyList<AskContextSourceBlock> SourceBlocks);

    private async Task<ContextExecutionResult> ExecuteContextRequestsAsync(AskContextWorkflowSnapshot snapshot, IReadOnlyList<AskContextWorkflowContextRequest> requests, HashSet<string> executedRequests, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var duplicateContextRequest = false;
        var repeatedSameContextRequest = false;
        var providedContextSource = new StringBuilder();
        var sourceBlocks = new List<AskContextSourceBlock>();
        foreach (var request in requests)
        {
            if (_debugAsk)
            {
                Console.WriteLine($"context_request_type={request.Type}");
                Console.WriteLine($"context_request_path={request.Path}");
                Console.WriteLine($"context_request_target={request.Target}");
            }

            if (IsSearchRequest(request.Type))
            {
                var searchBlocks = await ResolveSearchRequestAsync(request, cancellationToken);
                if (searchBlocks.Count > 0)
                {
                    foreach (var block in searchBlocks)
                    {
                        if (_debugAsk)
                        {
                            Console.WriteLine($"resolved_start={block.StartLine}");
                            Console.WriteLine($"resolved_end={block.EndLine}");
                            Console.WriteLine("match_kind=search_match");
                            Console.WriteLine("source_block_added=true");
                        }

                        var blockKey = $"{request.Path}|{block.StartLine}:{block.EndLine}";
                        if (executedRequests.Contains(blockKey))
                        {
                            duplicateContextRequest = true;
                            repeatedSameContextRequest = true;
                            continue;
                        }

                        executedRequests.Add(blockKey);
                        duplicateContextRequest = duplicateContextRequest || executedRequests.Contains(blockKey);

                        Console.WriteLine($"read_range path={request.Path} range={block.StartLine}:{block.EndLine}");
                        builder.AppendLine(block.SourceWithLineNumbers);
                        providedContextSource.AppendLine(block.Source);
                        sourceBlocks.Add(new AskContextSourceBlock(request.Path, $"{block.StartLine}:{block.EndLine}", block.Source));
                        if (_debugAsk)
                        {
                            Console.WriteLine($"source_block begin path={request.Path} range={block.StartLine}:{block.EndLine}");
                            Console.WriteLine(block.Source);
                            Console.WriteLine("source_block end");
                        }
                    }

                    if (sourceBlocks.Count > 0)
                    {
                        return new ContextExecutionResult(builder.ToString(), duplicateContextRequest, repeatedSameContextRequest, providedContextSource.ToString().TrimEnd(), sourceBlocks);
                    }

                    continue;
                }
            }

            var rangeResult = await ResolveRangeAsync(request.Path, request.Target, cancellationToken);
            var range = rangeResult.Range;
            var matchKind = rangeResult.MatchKind;
            if (range is null)
            {
                if (_debugAsk)
                {
                    Console.WriteLine("resolved_start=");
                    Console.WriteLine("resolved_end=");
                    Console.WriteLine("match_kind=");
                    Console.WriteLine("source_block_added=false");
                }

                continue;
            }

            var (startLine, endLine) = range.Value;
            var expandedRange = await ExpandBodyRangeAsync(request.Path, startLine, endLine, cancellationToken);
            var expandedStartLine = expandedRange.StartLine;
            var expandedEndLine = expandedRange.EndLine;
            if (_debugAsk)
            {
                Console.WriteLine($"resolved_start={expandedStartLine}");
                Console.WriteLine($"resolved_end={expandedEndLine}");
                Console.WriteLine($"match_kind={matchKind}");
            }

            var requestKey = $"{request.Path}|{expandedStartLine}:{expandedEndLine}";
            var isDuplicate = executedRequests.Contains(requestKey);
            duplicateContextRequest = duplicateContextRequest || isDuplicate;
            Console.WriteLine($"duplicate_context_request={duplicateContextRequest.ToString().ToLowerInvariant()}");
            Console.WriteLine($"target={request.Target} resolved_start={expandedStartLine} resolved_end={expandedEndLine} match_kind={matchKind}");
            if (isDuplicate)
            {
                repeatedSameContextRequest = true;
                continue;
            }

            executedRequests.Add(requestKey);

            Console.WriteLine($"read_range path={request.Path} range={expandedStartLine}:{expandedEndLine}");
            var fullPath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, request.Path));
            var lines = await System.IO.File.ReadAllLinesAsync(fullPath, cancellationToken);
            var blockBuilder = new StringBuilder();
            var firstLine = Math.Max(1, expandedStartLine);
            var lastLine = Math.Min(expandedEndLine, lines.Length);
            for (var lineIndex = firstLine; lineIndex <= lastLine; lineIndex++)
            {
                var line = lines[lineIndex - 1];
                builder.AppendLine($"{lineIndex}: {line}");
                providedContextSource.AppendLine(line);
                blockBuilder.AppendLine(line);
            }

            var blockSource = blockBuilder.ToString().TrimEnd('\r', '\n');
            var blockRange = $"{firstLine}:{lastLine}";
            sourceBlocks.Add(new AskContextSourceBlock(request.Path, blockRange, blockSource));
            if (_debugAsk)
            {
                Console.WriteLine($"source_block begin path={request.Path} range={blockRange}");
                Console.WriteLine(blockSource);
                Console.WriteLine("source_block end");
                Console.WriteLine("source_block_added=true");
            }
        }

        return new ContextExecutionResult(builder.ToString(), duplicateContextRequest, repeatedSameContextRequest, providedContextSource.ToString().TrimEnd(), sourceBlocks);
    }

    private static bool IsSearchRequest(string requestType)
    {
        return requestType.Equals("search_symbol", StringComparison.OrdinalIgnoreCase)
            || requestType.Equals("search_text", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyList<(int StartLine, int EndLine, string Source, string SourceWithLineNumbers)>> ResolveSearchRequestAsync(AskContextWorkflowContextRequest request, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, request.Path));
        if (!System.IO.File.Exists(fullPath))
        {
            return [];
        }

        var sourceLines = await System.IO.File.ReadAllLinesAsync(fullPath, cancellationToken);
        var matches = new List<(int StartLine, int EndLine, string Source, string SourceWithLineNumbers)>();
        var seenRanges = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var index in FindMatchingLineIndexes(sourceLines, request.Target))
        {
            var range = FindEnclosingTypeOrMethodRange(sourceLines, index);
            if (range is null)
            {
                continue;
            }

            var (startLine, endLine) = range.Value;
            var key = $"{startLine}:{endLine}";
            if (!seenRanges.Add(key))
            {
                continue;
            }

            matches.Add(BuildSourceBlock(sourceLines, startLine, endLine));
            var declarationRange = FindTypeOrMethodDeclarationRange(sourceLines, startLine, endLine);
            if (declarationRange is not null)
            {
                var declarationKey = $"{declarationRange.Value.StartLine}:{declarationRange.Value.EndLine}";
                if (seenRanges.Add(declarationKey))
                {
                    matches.Add(BuildSourceBlock(sourceLines, declarationRange.Value.StartLine, declarationRange.Value.EndLine));
                }
            }
        }

        return matches;
    }

    private static IEnumerable<int> FindMatchingLineIndexes(string[] sourceLines, string target)
    {
        for (var index = 0; index < sourceLines.Length; index++)
        {
            var line = sourceLines[index];
            if (line.Contains(target, StringComparison.OrdinalIgnoreCase))
            {
                yield return index;
            }
        }
    }

    private static (int StartLine, int EndLine)? FindEnclosingTypeOrMethodRange(string[] sourceLines, int matchIndex)
    {
        var startLine = -1;
        for (var index = matchIndex; index >= 0; index--)
        {
            if (IsTypeOrMethodDeclarationSourceLine(sourceLines[index], string.Empty))
            {
                startLine = index + 1;
                break;
            }
        }

        if (startLine < 0)
        {
            return null;
        }

        return ExpandBodyRange(sourceLines, startLine);
    }

    private static (int StartLine, int EndLine)? FindTypeOrMethodDeclarationRange(string[] sourceLines, int startLine, int endLine)
    {
        if (startLine < 1 || startLine > sourceLines.Length)
        {
            return null;
        }

        if (!IsTypeOrMethodDeclarationSourceLine(sourceLines[startLine - 1], string.Empty))
        {
            return null;
        }

        return ExpandBodyRange(sourceLines, startLine);
    }

    private static bool IsTypeOrMethodDeclarationSourceLine(string line, string target)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Contains("class ", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("record ", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(target) || trimmed.Contains(target, StringComparison.OrdinalIgnoreCase);
        }

        if (!trimmed.Contains("static", StringComparison.OrdinalIgnoreCase)
            && !trimmed.Contains("void", StringComparison.OrdinalIgnoreCase)
            && !trimmed.Contains("Task<", StringComparison.OrdinalIgnoreCase)
            && !trimmed.Contains("Task<int>", StringComparison.OrdinalIgnoreCase)
            && !trimmed.Contains("(", StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(target)
            && !trimmed.Contains($"{target}(", StringComparison.Ordinal)
            && !TargetMentionsMethod(trimmed, target)
            && !trimmed.Contains(target, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !trimmed.Contains("=>", StringComparison.Ordinal);
    }

    private static (int StartLine, int EndLine)? ExpandBodyRange(string[] sourceLines, int startLine)
    {
        if (startLine < 1 || startLine > sourceLines.Length)
        {
            return null;
        }

        var braceDepth = 0;
        var seenOpeningBrace = false;
        for (var index = startLine - 1; index < sourceLines.Length; index++)
        {
            var line = sourceLines[index];
            for (var charIndex = 0; charIndex < line.Length; charIndex++)
            {
                var character = line[charIndex];
                if (character == '{')
                {
                    braceDepth++;
                    seenOpeningBrace = true;
                }
                else if (character == '}' && seenOpeningBrace)
                {
                    braceDepth--;
                    if (braceDepth == 0)
                    {
                        return (startLine, index + 1);
                    }
                }
            }
        }

        return null;
    }

    private static (int StartLine, int EndLine, string Source, string SourceWithLineNumbers) BuildSourceBlock(string[] sourceLines, int startLine, int endLine)
    {
        var source = new StringBuilder();
        var lineNumbers = new StringBuilder();
        for (var lineIndex = startLine; lineIndex <= endLine; lineIndex++)
        {
            var line = sourceLines[lineIndex - 1];
            source.AppendLine(line);
            lineNumbers.AppendLine($"{lineIndex}: {line}");
        }

        return (startLine, endLine, source.ToString().TrimEnd('\r', '\n'), lineNumbers.ToString().TrimEnd('\r', '\n'));
    }

    private async Task<(int StartLine, int EndLine)> ExpandBodyRangeAsync(string path, int startLine, int endLine, CancellationToken cancellationToken)
    {
        if (startLine < 1 || endLine < startLine)
        {
            return (startLine, endLine);
        }

        var fullPath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, path));
        if (!System.IO.File.Exists(fullPath))
        {
            return (startLine, endLine);
        }

        var sourceLines = await System.IO.File.ReadAllLinesAsync(fullPath, cancellationToken);
        if (startLine > sourceLines.Length)
        {
            return (startLine, endLine);
        }

        var declarationLine = sourceLines[startLine - 1].TrimStart();
        if (!declarationLine.Contains("(", StringComparison.Ordinal) && !declarationLine.Contains("class ", StringComparison.OrdinalIgnoreCase) && !declarationLine.Contains("record ", StringComparison.OrdinalIgnoreCase))
        {
            return (startLine, endLine);
        }

        var braceDepth = 0;
        var seenOpeningBrace = false;
        for (var index = startLine - 1; index < sourceLines.Length; index++)
        {
            var line = sourceLines[index];
            for (var charIndex = 0; charIndex < line.Length; charIndex++)
            {
                var character = line[charIndex];
                if (character == '{')
                {
                    braceDepth++;
                    seenOpeningBrace = true;
                }
                else if (character == '}' && seenOpeningBrace)
                {
                    braceDepth--;
                    if (braceDepth == 0)
                    {
                        return (startLine, index + 1);
                    }
                }
            }
        }

        return (startLine, endLine);
    }

    private static string BuildDuplicateContextCorrectionPrompt(string userGoal, AskRepoMap repoMap, AskContextWorkflowSnapshot snapshot, string providedSnippet)
    {
        var builder = new StringBuilder();
        return BuildPostContextPrompt(userGoal, repoMap, snapshot, [], providedSnippet);
    }

    private static string BuildPostContextPrompt(string userGoal, AskRepoMap repoMap, AskContextWorkflowSnapshot snapshot, IReadOnlyList<AskContextWorkflowContextRequest> providedContext, string providedContextSource)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are the ASK context workflow.");
        builder.AppendLine("User goal:");
        builder.AppendLine(userGoal);
        builder.AppendLine("Repo map:");
        builder.AppendLine(repoMap.RenderedMap);
        builder.AppendLine("Snapshot:");
        builder.AppendLine(snapshot.Rendered);
        builder.AppendLine("Context provided:");
        foreach (var request in providedContext)
        {
            builder.AppendLine($"File: {request.Path}");
            builder.AppendLine($"Range: {request.Target}");
            builder.AppendLine($"Target: {request.Target}");
            builder.AppendLine($"Reason: {request.Reason}");
        }

        if (!string.IsNullOrWhiteSpace(providedContextSource))
        {
            builder.AppendLine("```csharp");
            builder.AppendLine(providedContextSource);
            builder.AppendLine("```");
        }

        builder.AppendLine("You already have RunAsync. Do not request it again.");
        builder.AppendLine("Do you need a different range, or are you ready_for_edit?");
        builder.AppendLine("Allowed response schemas only:");
        builder.AppendLine("{ \"kind\": \"context_request\", \"neededContext\": [ { \"type\": \"read_range|read_method|read_type|search_symbol|search_text|snapshot\", \"path\": \"...\", \"target\": \"...\", \"reason\": \"...\" } ] }");
        builder.AppendLine("{ \"kind\": \"ready_for_edit\", \"understanding\": \"...\", \"evidence\": [ { \"quote\": \"...\", \"meaning\": \"...\" } ], \"targetFiles\": [], \"targetRanges\": [] }");
        return builder.ToString();
    }

    private static string BuildChangeProposalCorrectionPrompt(string userGoal, AskRepoMap repoMap, AskContextWorkflowSnapshot snapshot, string providedContextSource, string providedContextSummary)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are the ASK context workflow.");
        builder.AppendLine("User goal:");
        builder.AppendLine(userGoal);
        builder.AppendLine("Repo map:");
        builder.AppendLine(repoMap.RenderedMap);
        builder.AppendLine("Snapshot:");
        builder.AppendLine(snapshot.Rendered);
        builder.AppendLine("Context provided:");
        builder.AppendLine("File: src/Ecp.Cli/Program.cs");
        builder.AppendLine("Range: 15:115");
        builder.AppendLine("Target: RunAsync");
        if (!string.IsNullOrWhiteSpace(providedContextSource))
        {
            builder.AppendLine("```csharp");
            builder.AppendLine(providedContextSource);
            builder.AppendLine("```");
        }
        builder.AppendLine("You provided an edit proposal too early. Do not propose code yet.");
        builder.AppendLine("Convert your response into ready_for_edit.");
        builder.AppendLine("Use evidence quotes from the provided RunAsync source.");
        builder.AppendLine("No changes[] field.");
        if (!string.IsNullOrWhiteSpace(providedContextSummary))
        {
            builder.AppendLine("Provided context summary:");
            builder.AppendLine(providedContextSummary);
        }

        builder.AppendLine("Allowed output:");
        builder.AppendLine("{\n  \"kind\": \"ready_for_edit\",\n  \"understanding\": \"...\",\n  \"evidence\": [\n    { \"quote\": \"...\", \"meaning\": \"...\" }\n  ],\n  \"targetFiles\": [],\n  \"targetRanges\": []\n}");
        return builder.ToString();
    }

    private static bool ContainsEntireFileRangeRequest(IReadOnlyList<AskContextWorkflowContextRequest> requests)
    {
        foreach (var request in requests)
        {
            if (request.Type.Equals("read_range", StringComparison.OrdinalIgnoreCase)
                && request.Target.Equals("entire_file", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildRangeTooBroadRepairPrompt(string userGoal, AskRepoMap repoMap, AskContextWorkflowSnapshot snapshot, IReadOnlyList<AskContextWorkflowContextRequest> requests)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are the ASK context workflow.");
        builder.AppendLine("User goal:");
        builder.AppendLine(userGoal);
        builder.AppendLine("Repo map:");
        builder.AppendLine(repoMap.RenderedMap);
        builder.AppendLine("Snapshot:");
        builder.AppendLine(snapshot.Rendered);
        builder.AppendLine("The requested range is too broad.");
        builder.AppendLine("Do not request entire_file.");
        builder.AppendLine("Request specific methods or text searches instead.");
        builder.AppendLine("For this task, request the HasFlag declaration and HasFlag call sites.");
        builder.AppendLine("Allowed response schemas only:");
        builder.AppendLine("{ \"kind\": \"context_request\", \"neededContext\": [ { \"type\": \"read_range|read_method|read_type|search_symbol|search_text|snapshot\", \"path\": \"...\", \"target\": \"...\", \"reason\": \"...\" } ] }");
        builder.AppendLine("{ \"kind\": \"ready_for_edit\", \"understanding\": \"...\", \"evidence\": [ { \"quote\": \"...\", \"meaning\": \"...\" } ], \"targetFiles\": [], \"targetRanges\": [] }");
        return builder.ToString();
    }

    private sealed record RangeResolution((int StartLine, int EndLine)? Range, string MatchKind);

    private async Task<RangeResolution> ResolveRangeAsync(string path, string target, CancellationToken cancellationToken)
    {
        var matchKind = string.Empty;
        if (target.Contains(':') || target.Contains('-'))
        {
            var normalized = target.Replace(':', '-');
            var parts = normalized.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], out var startLine) && int.TryParse(parts[1], out var endLine))
            {
                matchKind = "explicit_range";
                return new RangeResolution((startLine, endLine), matchKind);
            }
        }

        var fullPath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, path));
        if (!System.IO.File.Exists(fullPath))
        {
            return new RangeResolution(null, matchKind);
        }

        var sourceLines = await System.IO.File.ReadAllLinesAsync(fullPath, cancellationToken);
        for (var index = 0; index < sourceLines.Length; index++)
        {
            var line = sourceLines[index];
            if (!IsTypeOrMethodDeclarationLine(line, target))
            {
                continue;
            }

            var startLine = index + 1;
            var endLine = startLine;
            endLine = ExpandBodyRange(sourceLines, startLine)?.EndLine ?? startLine;

            matchKind = line.Contains("class ", StringComparison.OrdinalIgnoreCase) || line.Contains("record ", StringComparison.OrdinalIgnoreCase)
                ? "type_declaration"
                : "method_declaration";
            return new RangeResolution((startLine, endLine), matchKind);
        }

        return new RangeResolution(null, matchKind);
    }

    private static string NormalizeSnapshotLine(string line)
    {
        var colonIndex = line.IndexOf(':');
        if (colonIndex > 0 && int.TryParse(line[..colonIndex].Trim(), out _))
        {
            return line[(colonIndex + 1)..].TrimStart();
        }

        return line;
    }

    private static bool IsTypeOrMethodDeclarationLine(string line, string target)
    {
        var normalizedLine = NormalizeSnapshotLine(line);
        if (normalizedLine.Contains("class ", StringComparison.OrdinalIgnoreCase) || normalizedLine.Contains("record ", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(target) && !normalizedLine.Contains(target, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return normalizedLine.Contains("class ", StringComparison.OrdinalIgnoreCase) || normalizedLine.Contains("record ", StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(target)
            && !normalizedLine.Contains($"{target}(", StringComparison.Ordinal)
            && !TargetMentionsMethod(normalizedLine, target)
            && !normalizedLine.Contains(target, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return normalizedLine.Contains("static", StringComparison.OrdinalIgnoreCase)
            || normalizedLine.Contains("Task<int>", StringComparison.OrdinalIgnoreCase)
            || normalizedLine.Contains("Task<", StringComparison.OrdinalIgnoreCase)
            || normalizedLine.StartsWith("static", StringComparison.OrdinalIgnoreCase)
            || normalizedLine.Contains("class ", StringComparison.OrdinalIgnoreCase)
            || normalizedLine.Contains("record ", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractTypeName(string declarationLine)
    {
        var normalizedLine = NormalizeSnapshotLine(declarationLine);
        var tokens = normalizedLine.Split([' ', '\t', '{', ':', '<', '('], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = 0; index < tokens.Length - 1; index++)
        {
            if (tokens[index] is "class" or "record")
            {
                return tokens[index + 1];
            }
        }

        return string.Empty;
    }

    private static bool TargetMentionsMethod(string line, string target)
    {
        var openParenIndex = line.IndexOf('(');
        if (openParenIndex <= 0)
        {
            return false;
        }

        var prefix = line[..openParenIndex].TrimEnd();
        var lastSpaceIndex = prefix.LastIndexOf(' ');
        if (lastSpaceIndex < 0 || lastSpaceIndex == prefix.Length - 1)
        {
            return false;
        }

        var methodName = prefix[(lastSpaceIndex + 1)..];
        return target.Contains(methodName, StringComparison.OrdinalIgnoreCase);
    }

    private static int ParseLineNumber(string line)
    {
        var colonIndex = line.IndexOf(':');
        if (colonIndex > 0 && int.TryParse(line[..colonIndex].Trim(), out var lineNumber))
        {
            return lineNumber;
        }

        return 1;
    }

    private static AskContextWorkflowReadyForEdit? ParseReadyForEdit(string response)
    {
        using var document = JsonDocument.Parse(ExtractJsonPayload(response));
        var root = document.RootElement;
        if (TryGetKind(root) is not "ready_for_edit")
        {
            return null;
        }

        var evidence = new List<(string Quote, string Meaning)>();
        if (root.TryGetProperty("evidence", out var evidenceProp) && evidenceProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in evidenceProp.EnumerateArray())
            {
                var quote = item.TryGetProperty("quote", out var quoteProp) ? quoteProp.GetString() ?? string.Empty : string.Empty;
                var meaning = item.TryGetProperty("meaning", out var meaningProp) ? meaningProp.GetString() ?? string.Empty : string.Empty;
                evidence.Add((quote, meaning));
            }
        }

        var targetFiles = ReadStringArray(root, "targetFiles");
        var targetRanges = ReadStringArray(root, "targetRanges");
        return new AskContextWorkflowReadyForEdit(
            root.TryGetProperty("understanding", out var understandingProp) ? understandingProp.GetString() ?? string.Empty : string.Empty,
            evidence,
            targetFiles,
            targetRanges);
    }

    private static string? TryGetKind(JsonElement root)
    {
        if (root.TryGetProperty("kind", out var kindProp) && kindProp.ValueKind == JsonValueKind.String)
        {
            return kindProp.GetString();
        }

        return null;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                values.Add(item.GetString() ?? string.Empty);
            }
        }

        return values;
    }
}
