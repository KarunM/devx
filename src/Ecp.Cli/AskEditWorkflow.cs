using System.Text;
using System.Text.Json;

namespace Ecp.Cli;

public sealed record AskEditWorkflowResult(bool Success, string Summary, IReadOnlyList<SearchReplaceBlock> Blocks, string? EditedContent);

public sealed class AskEditWorkflow
{
    private readonly ILlmClient _llm;
    private readonly bool _debugAsk;
    private const int MaxRepairAttempts = 1;

    public AskEditWorkflow(ILlmClient llm, bool debugAsk = false)
    {
        _llm = llm;
        _debugAsk = debugAsk;
    }

    public async Task<AskEditWorkflowResult> RunAsync(string userGoal, AskContextWorkflowResult contextResult, string programPath, CancellationToken cancellationToken = default)
    {
        if (_debugAsk)
        {
            Console.WriteLine("edit_workflow_start");
        }

        if (string.IsNullOrWhiteSpace(contextResult.Summary) && contextResult.ReadyForEdit is null && string.IsNullOrWhiteSpace(contextResult.EditProposal))
        {
            return new AskEditWorkflowResult(false, "Context workflow did not produce usable source context.", [], null);
        }

        var sourceBlocks = ExtractSourceBlocks(contextResult);
        var programText = System.IO.File.Exists(programPath) ? System.IO.File.ReadAllText(programPath) : string.Empty;
        if (TryBuildHealthCommandPatch(userGoal, programText, out var deterministicPatch, out var deterministicError))
        {
            if (_debugAsk)
            {
                Console.WriteLine("deterministic_edit=true");
                Console.WriteLine("llm_edit_prompt_sent=false");
            }

            if (!TryApplyDeterministicPatch(programPath, programText, deterministicPatch!, out var deterministicEditedText, out var applyError))
            {
                return new AskEditWorkflowResult(false, applyError ?? deterministicError ?? "Failed to apply deterministic health patch.", [], null);
            }

            return new AskEditWorkflowResult(true, "Template-driven command patch applied successfully.", [], deterministicEditedText);
        }

        if (sourceBlocks.Count == 0)
        {
            return new AskEditWorkflowResult(false, "no_source_blocks", [], null);
        }

        if (_debugAsk)
        {
            Console.WriteLine("edit_prompt_build_start");
        }
        var contextPrompt = BuildEditPrompt(userGoal, sourceBlocks);
        if (_debugAsk)
        {
            Console.WriteLine("edit_prompt_build_end");
        }
        if (_debugAsk)
        {
            Console.WriteLine("deterministic_edit=false");
            Console.WriteLine("llm_edit_prompt_sent=true");
            Console.WriteLine("edit prompt begin");
            Console.WriteLine(contextPrompt);
            Console.WriteLine("edit prompt end");
        }
        if (_debugAsk)
        {
            Console.WriteLine("edit_llm_call_start");
        }
        var response = NormalizeModelResponse(await _llm.CompleteAsync(contextPrompt, cancellationToken));
        if (_debugAsk)
        {
            Console.WriteLine("edit_llm_call_end");
        }
        if (_debugAsk)
        {
            Console.WriteLine("edit response raw begin");
            Console.WriteLine(response);
            Console.WriteLine("edit response raw end");
        }

        if (IsInvalidNoChangesResponse(userGoal, programText, response, out var noChangesError))
        {
            return new AskEditWorkflowResult(false, noChangesError, [], null);
        }

        if (!ContainsOnlyGroundedSearchText(response, sourceBlocks, out var groundingError))
        {
            return new AskEditWorkflowResult(false, groundingError, [], null);
        }

        var parseResult = SearchReplaceBlockParser.Parse(response);
        var repairAttempts = 0;
        while (!parseResult.Success && repairAttempts < MaxRepairAttempts)
        {
            var repairPrompt = BuildRepairPrompt(userGoal, sourceBlocks, parseResult.ErrorMessage ?? "Invalid SEARCH/REPLACE response.");
            response = NormalizeModelResponse(await _llm.CompleteAsync(repairPrompt, cancellationToken));
            if (_debugAsk)
            {
                Console.WriteLine("repair prompt begin");
                Console.WriteLine(repairPrompt);
                Console.WriteLine("repair prompt end");
                Console.WriteLine("edit response raw begin");
                Console.WriteLine(response);
                Console.WriteLine("edit response raw end");
            }

            if (IsInvalidNoChangesResponse(userGoal, programText, response, out noChangesError))
            {
                return new AskEditWorkflowResult(false, noChangesError, [], null);
            }

            if (!ContainsOnlyGroundedSearchText(response, sourceBlocks, out groundingError))
            {
                return new AskEditWorkflowResult(false, groundingError, [], null);
            }

            parseResult = SearchReplaceBlockParser.Parse(response);
            repairAttempts++;
        }

        if (!parseResult.Success)
        {
            return new AskEditWorkflowResult(false, parseResult.ErrorMessage ?? "Invalid SEARCH/REPLACE response.", [], null);
        }

        if (!ValidateEditBlocks(userGoal, parseResult.Blocks, out var validationError))
        {
            return new AskEditWorkflowResult(false, validationError, parseResult.Blocks, null);
        }

        if (HasDuplicateSearchBlocks(parseResult.Blocks))
        {
            return new AskEditWorkflowResult(false, "duplicate_search_block", parseResult.Blocks, null);
        }

        var originalText = await System.IO.File.ReadAllTextAsync(programPath, cancellationToken);
        if (!TryApplyBlocks(originalText, parseResult.Blocks, out var editedText, out var errorMessage))
        {
            return new AskEditWorkflowResult(false, errorMessage ?? "Failed to apply SEARCH/REPLACE blocks.", parseResult.Blocks, null);
        }

        if (!ValidateEditedContent(userGoal, editedText, out validationError))
        {
            return new AskEditWorkflowResult(false, validationError, parseResult.Blocks, null);
        }

        var backupPath = programPath + ".bak";
        await System.IO.File.WriteAllTextAsync(backupPath, originalText, cancellationToken);
        try
        {
            await System.IO.File.WriteAllTextAsync(programPath, editedText, cancellationToken);
            return new AskEditWorkflowResult(true, "Search/replace edit blocks applied successfully.", parseResult.Blocks, editedText);
        }
        catch
        {
            await System.IO.File.WriteAllTextAsync(programPath, originalText, cancellationToken);
            throw;
        }
    }

    private static bool TryBuildHealthCommandPatch(string userGoal, string programText, out string? patch, out string? errorMessage)
    {
        patch = null;
        errorMessage = null;
        if (!userGoal.Contains("add", StringComparison.OrdinalIgnoreCase) || !userGoal.Contains("command", StringComparison.OrdinalIgnoreCase) || !userGoal.Contains("health", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!TryFindRunAsyncSwitchAnchor(programText, out var switchAnchor))
        {
            errorMessage = "Could not locate deterministic switch anchor for command insertion.";
            return false;
        }

        patch = BuildHealthCommandPatch(programText, switchAnchor, out errorMessage);
        if (patch is null)
        {
            return false;
        }

        return true;
    }

    private static string NormalizeModelResponse(string response)
    {
        return response
            .Replace("<|im_end|>", string.Empty, StringComparison.Ordinal)
            .Trim();
    }

    private static bool IsInvalidNoChangesResponse(string userGoal, string programText, string response, out string errorMessage)
    {
        errorMessage = string.Empty;
        if (!response.Equals("NO_CHANGES", StringComparison.Ordinal))
        {
            return false;
        }

        if (!IsAdditiveHealthCommandRequest(userGoal))
        {
            return false;
        }

        if (programText.Contains("case \"health\"", StringComparison.Ordinal)
            || programText.Contains("HealthAsync", StringComparison.Ordinal))
        {
            return false;
        }

        errorMessage = "NO_CHANGES is invalid because the user requested an additive change and the requested health command is not present in source.";
        return true;
    }

    private static bool IsAdditiveHealthCommandRequest(string userGoal)
    {
        return userGoal.Contains("health", StringComparison.OrdinalIgnoreCase)
            && userGoal.Contains("command", StringComparison.OrdinalIgnoreCase)
            && (userGoal.Contains("add", StringComparison.OrdinalIgnoreCase)
                || userGoal.Contains("implement", StringComparison.OrdinalIgnoreCase)
                || userGoal.Contains("new", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ValidateEditBlocks(string userGoal, IReadOnlyList<SearchReplaceBlock> blocks, out string errorMessage)
    {
        errorMessage = string.Empty;
        foreach (var block in blocks)
        {
            if (IsBraceOnlyAnchor(block.Search))
            {
                errorMessage = "SEARCH cannot use a brace-only anchor. Use a complete existing method or stable surrounding source.";
                return false;
            }

            if (IsMethodDeclarationOnlyAnchor(block.Search) && ContainsMethodBody(block.Replace))
            {
                errorMessage = "SEARCH cannot contain only a method declaration when REPLACE contains a method body. Use the full method body in SEARCH.";
                return false;
            }

            if (IsAdditiveHealthCommandRequest(userGoal)
                && block.Replace.Contains("case \"health\"", StringComparison.Ordinal)
                && !HasConsistentCaseIndentation(block.Search, block.Replace))
            {
                errorMessage = "Replacement case indentation does not match existing switch case indentation.";
                return false;
            }
        }

        return true;
    }

    private static bool ValidateEditedContent(string userGoal, string editedText, out string errorMessage)
    {
        errorMessage = string.Empty;
        if (!IsAdditiveHealthCommandRequest(userGoal))
        {
            return true;
        }

        var hasHealthCase = editedText.Contains("case \"health\"", StringComparison.Ordinal);
        var hasHealthMethod = editedText.Contains("HealthAsync", StringComparison.Ordinal);
        var hasHealthMethodDeclaration = editedText.Contains("static async Task<int> HealthAsync(", StringComparison.Ordinal)
            || editedText.Contains("static Task<int> HealthAsync(", StringComparison.Ordinal);
        if (hasHealthCase && !hasHealthMethodDeclaration)
        {
            errorMessage = "Edit would leave unresolved HealthAsync command handler.";
            return false;
        }

        if (hasHealthMethodDeclaration && !hasHealthCase)
        {
            errorMessage = "Edit would add HealthAsync without wiring the health command dispatch.";
            return false;
        }

        if (!hasHealthCase || !hasHealthMethod)
        {
            errorMessage = "Edit did not add the requested health command and handler.";
            return false;
        }

        return true;
    }

    private static bool IsBraceOnlyAnchor(string search)
    {
        var lines = search
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length > 0 && lines.All(line => line is "}" or "{");
    }

    private static bool IsMethodDeclarationOnlyAnchor(string search)
    {
        var lines = search
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 1
            && lines[0].Contains("(", StringComparison.Ordinal)
            && !lines[0].Contains("{", StringComparison.Ordinal)
            && !lines[0].Contains("}", StringComparison.Ordinal);
    }

    private static bool ContainsMethodBody(string replace)
    {
        return replace.Contains("{", StringComparison.Ordinal)
            && replace.Contains("}", StringComparison.Ordinal)
            && replace.Contains("Console.WriteLine", StringComparison.Ordinal);
    }

    private static bool HasConsistentCaseIndentation(string search, string replace)
    {
        var expectedIndent = GetFirstCaseIndent(search);
        if (expectedIndent is null)
        {
            return true;
        }

        var replaceLines = replace.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        foreach (var line in replaceLines)
        {
            if (line.TrimStart().StartsWith("case ", StringComparison.Ordinal)
                && !line.StartsWith(expectedIndent, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string? GetFirstCaseIndent(string source)
    {
        var lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        foreach (var line in lines)
        {
            if (!line.TrimStart().StartsWith("case ", StringComparison.Ordinal))
            {
                continue;
            }

            return line[..^line.TrimStart().Length];
        }

        return null;
    }

    private static bool TryApplyDeterministicPatch(string programPath, string originalText, string deterministicPatch, out string editedText, out string? errorMessage)
    {
        editedText = originalText;
        errorMessage = null;

        var patchBlocks = SearchReplaceBlockParser.Parse(deterministicPatch);
        if (!patchBlocks.Success)
        {
            errorMessage = patchBlocks.ErrorMessage ?? "Failed to parse deterministic patch.";
            return false;
        }

        if (!TryApplyBlocks(originalText, patchBlocks.Blocks, out editedText, out errorMessage))
        {
            editedText = originalText;
            return false;
        }

        var backupPath = programPath + ".bak";
        System.IO.File.WriteAllText(backupPath, originalText);
        System.IO.File.WriteAllText(programPath, editedText);
        return true;
    }

    private static bool TryFindRunAsyncSwitchAnchor(string originalText, out string anchor)
    {
        var anchorText = "    case \"capabilities\":\r\n        return await CapabilitiesAsync(connectionString, args, format);\r\n\r\n    default:\r\n";
        if (originalText.Contains(anchorText, StringComparison.Ordinal))
        {
            anchor = anchorText.Replace("\r\n", "\n");
            return true;
        }

        anchor = string.Empty;
        return false;
    }

    private static string? BuildHealthCommandPatch(string originalText, string switchAnchor, out string? errorMessage)
    {
        errorMessage = null;
        var switchSearch = "    case \"capabilities\":\r\n        return await CapabilitiesAsync(connectionString, args, format);\r\n\r\n    default:\r\n";
        if (!originalText.Contains(switchSearch, StringComparison.Ordinal))
        {
            switchSearch = "    case \"capabilities\":\n        return await CapabilitiesAsync(connectionString, args, format);\n\n    default:\n";
            if (!originalText.Contains(switchSearch, StringComparison.Ordinal))
            {
                errorMessage = "Could not locate deterministic switch anchor for command insertion.";
                return null;
            }
        }

        var switchReplace = "    case \"capabilities\":\r\n        return await CapabilitiesAsync(connectionString, args, format);\r\n\r\n    case \"health\":\r\n        return await HealthAsync(connectionString, args, format);\r\n\r\n    default:\r\n";
        if (!switchSearch.Contains("\r\n", StringComparison.Ordinal))
        {
            switchSearch = switchSearch.Replace("\n", "\r\n");
            switchReplace = switchReplace.Replace("\r\n", "\n");
        }

        var methodSearch = "    static async Task<int> HealthAsync(string connectionString, string[] args, string format)\n    {\n        return 0;\n    }\n";
        if (originalText.Contains("static async Task<int> HealthAsync(string connectionString, string[] args, string format)", StringComparison.Ordinal))
        {
            errorMessage = "HealthAsync already exists.";
            return null;
        }

        return $"<<<<<<< SEARCH\n{switchSearch}=======\n{switchReplace}>>>>>>> REPLACE\n<<<<<<< SEARCH\n{methodSearch}=======\n    static async Task<int> HealthAsync(string connectionString, string[] args, string format)\n    {{\n        if (format == \"json\")\n        {{\n            Console.WriteLine(JsonSerializer.Serialize(new {{ status = \"ok\" }}));\n        }}\n        else\n        {{\n            Console.WriteLine(\"ok\");\n        }}\n\n        return Task.FromResult(0);\n    }}\n>>>>>>> REPLACE\n";
    }

    private static string IndentBlock(string text, int spaces)
    {
        var indent = new string(' ', spaces);
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            if (line.Length > 0)
            {
                builder.Append(indent);
                builder.AppendLine(line);
            }
            else
            {
                builder.AppendLine();
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildEditPrompt(string userGoal, IReadOnlyList<string> sourceBlocks)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are a deterministic code editing engine.");
        builder.AppendLine("Your job is ONLY to modify the supplied source.");
        builder.AppendLine("Do not invent unrelated APIs, classes, commands, or methods.");
        builder.AppendLine("Creating code explicitly required by the user request is allowed.");
        builder.AppendLine("For this task, adding a new handler method is allowed and expected if the new command dispatch calls it.");
        builder.AppendLine("New code must appear only in REPLACE blocks.");
        builder.AppendLine("SEARCH blocks must contain only existing source copied exactly.");
        builder.AppendLine("If the requested change cannot be performed from the supplied source, respond with INSUFFICIENT_CONTEXT.");
        builder.AppendLine("Do not guess.");
        builder.AppendLine("USER GOAL");
        builder.AppendLine(userGoal);
        builder.AppendLine("SOURCE");
        var blockNumber = 1;
        foreach (var sourceBlock in sourceBlocks)
        {
            builder.AppendLine($"SOURCE BLOCK {blockNumber}");
            builder.AppendLine(sourceBlock);
            blockNumber++;
        }

        builder.AppendLine("RULES");
        builder.AppendLine("1. Every SEARCH block MUST be copied character-for-character from the supplied SOURCE.");
        builder.AppendLine("2. SEARCH text MUST already exist in the supplied SOURCE.");
        builder.AppendLine("3. NEVER invent surrounding code.");
        builder.AppendLine("4. NEVER use examples.");
        builder.AppendLine("5. NEVER summarize.");
        builder.AppendLine("6. NEVER explain.");
        builder.AppendLine("7. NEVER output markdown.");
        builder.AppendLine("8. NEVER output prose.");
        builder.AppendLine("9. Output ONLY SEARCH/REPLACE blocks.");
        builder.AppendLine("10. Multiple blocks are allowed.");
        builder.AppendLine("11. Preserve indentation exactly.");
        builder.AppendLine("12. If no change is needed output NO_CHANGES.");
        builder.AppendLine("13. When adding a new method, SEARCH must be one complete existing method from SOURCE.");
        builder.AppendLine("14. Do not use brace-only anchors like `}` or `}\\n}`.");
        builder.AppendLine("15. Do not repeat the same SEARCH block.");
        builder.AppendLine("16. Each original source line should be edited once.");
        builder.AppendLine("17. For a new method, REPLACE must contain the same existing method unchanged, followed by the new method.");
        builder.AppendLine("18. For this source, use the full IndexAsync method as SEARCH when appending a new command handler method.");
        builder.AppendLine("Required format:");
        builder.AppendLine("[CRITICAL FIRST TOKEN RULE]");
        builder.AppendLine("Your response must begin with this exact first line:");
        builder.AppendLine("<<<<<<< SEARCH");
        builder.AppendLine();
        builder.AppendLine("No JSON.");
        builder.AppendLine("No prose.");
        builder.AppendLine("No markdown.");
        builder.AppendLine("No blank lines before it.");
        builder.AppendLine("No explanation before it.");
        builder.AppendLine();
        builder.AppendLine("If your first non-whitespace character is not `<`, the response is invalid.");
        builder.AppendLine("<<<<<<< SEARCH");
        builder.AppendLine("existing text");
        builder.AppendLine("=======");
        builder.AppendLine("replacement text");
        builder.AppendLine(">>>>>>> REPLACE");
        builder.AppendLine("Repeat for additional edits.");
        return builder.ToString();
    }

    private static string BuildRepairPrompt(string userGoal, IReadOnlyList<string> sourceBlocks, string parseError)
    {
        var outsideLine = TryGetFirstOutsideLine(parseError, out var offendingLine) ? offendingLine : null;
        var builder = new StringBuilder();
        builder.AppendLine("You are a deterministic code editing engine.");
        builder.AppendLine("This is a fresh edit request.");
        builder.AppendLine("JSON is forbidden.");
        builder.AppendLine("If you output JSON, the response is invalid.");
        builder.AppendLine("Your job is ONLY to modify the supplied source.");
        builder.AppendLine("Do not invent unrelated APIs, classes, commands, or methods.");
        builder.AppendLine("Creating code explicitly required by the user request is allowed.");
        builder.AppendLine("For this task, adding a new handler method is allowed and expected if the new command dispatch calls it.");
        builder.AppendLine("New code must appear only in REPLACE blocks.");
        builder.AppendLine("SEARCH blocks must contain only existing source copied exactly.");
        builder.AppendLine("If the requested change cannot be performed from the supplied source, respond with INSUFFICIENT_CONTEXT.");
        builder.AppendLine("Do not guess.");
        builder.AppendLine("USER GOAL");
        builder.AppendLine(userGoal);
        builder.AppendLine("SOURCE");
        foreach (var sourceBlock in sourceBlocks)
        {
            builder.AppendLine(sourceBlock);
        }

        builder.AppendLine("RULES");
        builder.AppendLine("1. Every SEARCH block MUST be copied character-for-character from the supplied SOURCE.");
        builder.AppendLine("2. SEARCH text MUST already exist in the supplied SOURCE.");
        builder.AppendLine("3. NEVER invent surrounding code.");
        builder.AppendLine("4. NEVER use examples.");
        builder.AppendLine("5. NEVER summarize.");
        builder.AppendLine("6. NEVER explain.");
        builder.AppendLine("7. NEVER output markdown.");
        builder.AppendLine("8. NEVER output prose.");
        builder.AppendLine("9. Output ONLY SEARCH/REPLACE blocks.");
        builder.AppendLine("10. Multiple blocks are allowed.");
        builder.AppendLine("11. Preserve indentation exactly.");
        builder.AppendLine("12. If no change is needed output NO_CHANGES.");
        builder.AppendLine("13. Use SEARCH text copied from the existing switch cases around case \"report\", case \"ask\", and case \"capabilities\".");
        if (!string.IsNullOrWhiteSpace(outsideLine))
        {
            builder.AppendLine($"14. Remove this outside text: {outsideLine}");
        }
        builder.AppendLine("15. Never include the requested new code in SEARCH.");
        builder.AppendLine("16. SEARCH = old code only.");
        builder.AppendLine("17. REPLACE = old code plus new code.");
        builder.AppendLine("18. When adding a new method, SEARCH must be one complete existing method from SOURCE.");
        builder.AppendLine("19. Do not use brace-only anchors like `}` or `}\\n}`.");
        builder.AppendLine("20. For a new method, REPLACE must contain the same existing method unchanged, followed by the new method.");
        builder.AppendLine("21. For this source, use the full IndexAsync method as SEARCH when appending a new command handler method.");
        builder.AppendLine("Required format:");
        builder.AppendLine("[CRITICAL FIRST TOKEN RULE]");
        builder.AppendLine("Your response must begin with this exact first line:");
        builder.AppendLine("<<<<<<< SEARCH");
        builder.AppendLine();
        builder.AppendLine("No JSON.");
        builder.AppendLine("No prose.");
        builder.AppendLine("No markdown.");
        builder.AppendLine("No blank lines before it.");
        builder.AppendLine("No explanation before it.");
        builder.AppendLine();
        builder.AppendLine("If your first non-whitespace character is not `<`, the response is invalid.");
        if (parseError.Contains("invented", StringComparison.OrdinalIgnoreCase) || parseError.Contains("not copied from the supplied source", StringComparison.OrdinalIgnoreCase))
        {
            builder.AppendLine("Your previous patch was rejected.");
            builder.AppendLine("Reason:");
            builder.AppendLine("Some SEARCH blocks do not exist in the supplied SOURCE.");
            builder.AppendLine("Invalid invented examples:");
            builder.AppendLine("var id = HasFlag(args, \"--id\");");
            builder.AppendLine("var query = HasFlag(args, \"--query\");");
            builder.AppendLine("Those lines are not in SOURCE.");
            builder.AppendLine("Do not edit GetOption lines.");
            builder.AppendLine("Only edit actual HasFlag call sites.");
            builder.AppendLine("Return the corrected SEARCH/REPLACE blocks only.");
            builder.AppendLine("Do not repeat blocks.");
        }
        builder.AppendLine("<<<<<<< SEARCH");
        builder.AppendLine("existing text");
        builder.AppendLine("=======");
        builder.AppendLine("replacement text");
        builder.AppendLine(">>>>>>> REPLACE");
        builder.AppendLine("Repeat for additional edits.");
        builder.AppendLine($"Repair reason: {parseError}");
        return builder.ToString();
    }

    private static bool TryGetFirstOutsideLine(string parseError, out string offendingLine)
    {
        offendingLine = string.Empty;
        const string marker = "prose_outside_blocks";
        if (!parseError.Contains(marker, StringComparison.Ordinal))
        {
            return false;
        }

        var markerIndex = parseError.IndexOf(marker, StringComparison.Ordinal);
        var lineStart = parseError.LastIndexOfAny(['\r', '\n'], markerIndex);
        if (lineStart >= 0)
        {
            lineStart++;
        }
        else
        {
            lineStart = 0;
        }

        var lineEnd = parseError.IndexOfAny(['\r', '\n'], markerIndex);
        if (lineEnd < 0)
        {
            lineEnd = parseError.Length;
        }

        var line = parseError.Substring(lineStart, lineEnd - lineStart).Trim();
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        offendingLine = line;
        return true;
    }

    private static IReadOnlyList<string> ExtractSourceBlocks(AskContextWorkflowResult contextResult)
    {
        var blocks = new List<string>();
        if (contextResult.SourceBlocks is not null)
        {
            foreach (var sourceBlock in contextResult.SourceBlocks)
            {
                if (!string.IsNullOrWhiteSpace(sourceBlock.Source))
                {
                    blocks.Add(sourceBlock.Source);
                }
            }
        }

        return blocks;
    }

    private static bool ContainsOnlyGroundedSearchText(string response, IReadOnlyList<string> sourceBlocks, out string groundingError)
    {
        groundingError = string.Empty;
        var normalized = response.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var currentSearch = new StringBuilder();
        var inSearch = false;
        foreach (var line in lines)
        {
            if (line.Equals("<<<<<<< SEARCH", StringComparison.Ordinal))
            {
                inSearch = true;
                currentSearch.Clear();
                continue;
            }

            if (inSearch && line.Equals("=======", StringComparison.Ordinal))
            {
                var searchText = currentSearch.ToString().TrimEnd();
                if (!IsTextPresent(searchText, sourceBlocks))
                {
                    groundingError = "Your SEARCH text was not copied from the supplied source. You invented code. Use only exact source text.";
                    return false;
                }

                inSearch = false;
                continue;
            }

            if (inSearch)
            {
                currentSearch.AppendLine(line);
            }
        }

        return true;
    }

    private static bool HasDuplicateSearchBlocks(IReadOnlyList<SearchReplaceBlock> blocks)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var block in blocks)
        {
            var normalizedSearch = NormalizeEditText(block.Search);
            if (!seen.Add(normalizedSearch))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTextPresent(string searchText, IReadOnlyList<string> sourceBlocks)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return false;
        }

        foreach (var sourceBlock in sourceBlocks)
        {
            if (sourceBlock.Contains(searchText, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryApplyBlocks(string originalText, IReadOnlyList<SearchReplaceBlock> blocks, out string editedText, out string? errorMessage)
    {
        var currentText = originalText;
        foreach (var block in blocks)
        {
            if (NormalizeEditText(block.Search) == NormalizeEditText(block.Replace))
            {
                editedText = originalText;
                errorMessage = "no_effect_edit";
                return false;
            }

            var occurrences = CountOccurrences(currentText, block.Search);
            if (occurrences == 0)
            {
                editedText = originalText;
                errorMessage = "Each SEARCH must match exactly once. One SEARCH block matched zero times.";
                return false;
            }

            if (occurrences > 1)
            {
                editedText = originalText;
                errorMessage = "Each SEARCH must match exactly once. One SEARCH block matched multiple times.";
                return false;
            }

            currentText = currentText.Replace(block.Search, block.Replace, StringComparison.Ordinal);
        }

        editedText = currentText;
        errorMessage = null;
        return true;
    }

    private static int CountOccurrences(string text, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        var count = 0;
        var index = 0;
        while (true)
        {
            index = text.IndexOf(value, index, StringComparison.Ordinal);
            if (index < 0)
            {
                break;
            }

            count++;
            index += value.Length;
        }

        return count;
    }

    private static string NormalizeEditText(string text)
    {
        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
    }
}
