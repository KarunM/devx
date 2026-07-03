using System.Text.Json;
using System.Text.RegularExpressions;
using Ecp.Domain;
using Ecp.Protocol;

namespace Ecp.Cli;

public sealed record AskResult(string Summary, bool Success, IReadOnlyList<AskTurn> TurnHistory);
public sealed record AskTurn(string Role, string Content, string? ToolName = null, string? ToolResult = null);

public sealed class AskOrchestrator
{
    private readonly ILlmClient _llm;
    private readonly IProtocolEngine _protocolEngine;
    private readonly LlmConfig _config;
    private readonly string? _sandboxRoot;

    public event EventHandler<AskTurn>? OnTurn;

    public AskOrchestrator(ILlmClient llm, IProtocolEngine protocolEngine, LlmConfig config, string? sandboxRoot = null)
    {
        _llm = llm;
        _protocolEngine = protocolEngine;
        _config = config;
        _sandboxRoot = sandboxRoot;
    }

    public async Task<AskResult> RunAsync(string userPrompt, SessionState session, CancellationToken cancellationToken = default)
    {
        var turns = new List<AskTurn>();
        var conversation = new List<ConversationMessage>
        {
            new("system", BuildSystemPrompt()),
            new("user", userPrompt)
        };
        var memory = new System.Text.StringBuilder();

        var turnCount = 0;
        const int maxTurns = 30;
        var sessionStart = DateTimeOffset.UtcNow;
        var maxSessionDuration = TimeSpan.FromMinutes(10);

        while (turnCount < maxTurns)
        {
            if (DateTimeOffset.UtcNow - sessionStart > maxSessionDuration)
            {
                return new AskResult("Session timed out after 10 minutes. The task may be incomplete.", false, turns);
            }

            turnCount++;
            var prompt = FormatConversationForLlm(conversation, memory);
            var response = await _llm.CompleteAsync(prompt, cancellationToken);

            var toolCall = ParseToolCall(response);
            if (toolCall is null)
            {
                var turn = new AskTurn("assistant", response);
                conversation.Add(new ConversationMessage("assistant", response));
                turns.Add(turn);
                OnTurn?.Invoke(this, turn);
                TrimConversation(conversation, memory, _config.ConversationWindow);
                continue;
            }

            if (toolCall.Tool == "done")
            {
                var summary = SafeGetString(toolCall.Args, "summary") ?? "Task completed.";
                var turn = new AskTurn("assistant", summary, "done");
                turns.Add(turn);
                OnTurn?.Invoke(this, turn);
                return new AskResult(summary, true, turns);
            }

            if (toolCall.Tool == "__json_error")
            {
                var errorMsg = SafeGetString(toolCall.Args, "error") ?? "Invalid JSON";
                var rawMsg = SafeGetString(toolCall.Args, "raw") ?? "";
                var turn = new AskTurn("assistant", response);
                conversation.Add(new ConversationMessage("assistant", response));
                conversation.Add(new ConversationMessage("system", $"Your tool call was malformed: {errorMsg}. Raw: {rawMsg}. Fix and retry."));
                turns.Add(turn);
                OnTurn?.Invoke(this, turn);
                TrimConversation(conversation, memory, _config.ConversationWindow);
                continue;
            }

            var assistantTurn = new AskTurn("assistant", response, toolCall.Tool);
            turns.Add(assistantTurn);
            OnTurn?.Invoke(this, assistantTurn);
            conversation.Add(new ConversationMessage("assistant", response));

            var toolResult = await ExecuteToolAsync(toolCall, session, cancellationToken);
            var toolResultJson = JsonSerializer.Serialize(toolResult);
            var shouldTruncate = toolCall.Tool is "search_symbol" or "search_text" or "callers" or "callees";
            var truncatedResult = shouldTruncate && toolResultJson.Length > _config.MaxToolResultLength
                ? toolResultJson[.._config.MaxToolResultLength] + "\n...[truncated]"
                : toolResultJson;
            var toolTurn = new AskTurn("tool", truncatedResult, toolCall.Tool, truncatedResult);
            turns.Add(toolTurn);
            OnTurn?.Invoke(this, toolTurn);
            conversation.Add(new ConversationMessage("tool", truncatedResult));
            TrimConversation(conversation, memory, _config.ConversationWindow);
        }

        return new AskResult("Reached maximum turn limit. The task may be incomplete.", false, turns);
    }

    private static void TrimConversation(List<ConversationMessage> conversation, System.Text.StringBuilder memory, int window)
    {
        if (conversation.Count <= 2 + window * 2) return;
        var dropStart = 2;
        var dropCount = conversation.Count - 2 - window * 2;
        if (dropCount <= 0) return;

        var sb = new System.Text.StringBuilder();
        sb.Append("Previously: ");
        for (var i = dropStart; i < dropStart + dropCount && i < conversation.Count; i++)
        {
            var msg = conversation[i];
            if (msg.Role == "assistant" && msg.Content.Contains("<tool_call>"))
            {
                var toolName = ExtractToolName(msg.Content);
                if (!string.IsNullOrEmpty(toolName)) sb.Append($"called {toolName}, ");
            }
        }
        if (sb.Length > 0)
        {
            sb.Length -= 2;
            sb.Append(".");
            if (memory.Length > 0) memory.Append(" ");
            memory.Append(sb.ToString());
        }

        conversation.RemoveRange(dropStart, dropCount);
    }

    private static string? ExtractToolName(string content)
    {
        var match = Regex.Match(content, @"<tool_call>\s*\{\s*""name""\s*:\s*""(\w+)""");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string FormatConversationForLlm(List<ConversationMessage> conversation, System.Text.StringBuilder memory)
    {
        var sb = new System.Text.StringBuilder();
        if (memory.Length > 0)
        {
            sb.Append($"\n<system>\nMemory: {memory}\n</system>\n");
        }
        foreach (var msg in conversation)
        {
            sb.Append($"\n<{msg.Role}>\n{msg.Content}\n</{msg.Role}>\n");
        }
        return sb.ToString();
    }

    private static string BuildSystemPrompt()
    {
        return "You are a coding agent with tools. Do not think. Immediately output the tool call.\n\n" +
            "Tools:\n" +
            "- search_symbol(query): find code by name\n" +
            "- search_text(query): search files\n" +
            "- read_file(query): read file\n" +
            "- build(): build project\n" +
            "- test(): run tests\n" +
            "- done(summary): finish\n\n" +
            "Format:\n" +
            "<tool_call>\n" +
            "{\"name\": \"TOOL_NAME\", \"arguments\": {\"key\": \"value\"}}\n" +
            "</tool_call>\n\n" +
            "Example:\n" +
            "<tool_call>\n" +
            "{\"name\": \"search_symbol\", \"arguments\": {\"query\": \"Foo\"}}\n" +
            "</tool_call>";
    }

    private static ToolCall? ParseToolCall(string response)
    {
        // Extract content after thinking markers
        var cleaned = response;
        
        // Remove thinking sections (Qwen3 style)
        var thinkEnd = cleaned.LastIndexOf("</think>");
        if (thinkEnd >= 0)
        {
            cleaned = cleaned[(thinkEnd + "</think>".Length)..];
        }
        
        // Remove code blocks
        cleaned = Regex.Replace(cleaned, @"```(?:xml|json)?\s*", string.Empty, RegexOptions.Singleline);
        cleaned = cleaned.Replace("```", string.Empty);
        cleaned = cleaned.Trim();

        // Find <tool_call> tag
        var toolCallMatch = Regex.Match(cleaned, @"<tool_call>\s*(\{.*?\})\s*</tool_call>", RegexOptions.Singleline);
        if (!toolCallMatch.Success)
        {
            // Try without XML tags
            toolCallMatch = Regex.Match(cleaned, @"\{\s*""name""\s*:\s*""([^""]+)""\s*,\s*""arguments""\s*:\s*(\{.*?\})\s*\}", RegexOptions.Singleline);
            if (!toolCallMatch.Success) return null;
            
            try
            {
                var toolName = toolCallMatch.Groups[1].Value;
                var argsJson = toolCallMatch.Groups[2].Value;
                using var doc = JsonDocument.Parse(argsJson);
                return new ToolCall(toolName, doc.RootElement.Clone());
            }
            catch { return null; }
        }

        try
        {
            var json = toolCallMatch.Groups[1].Value;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            
            if (root.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
            {
                var toolName = nameProp.GetString()!;
                var args = root.TryGetProperty("arguments", out var argsProp) 
                    ? argsProp.Clone() 
                    : JsonDocument.Parse("{}").RootElement.Clone();
                return new ToolCall(toolName, args);
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private async Task<ProtocolResponse> ExecuteToolAsync(ToolCall call, SessionState session, CancellationToken cancellationToken)
    {
        try
        {
            if (call.Tool == "patch_status")
            {
                return await HandlePatchStatusAsync();
            }

            var operation = call.Tool switch
            {
                "search_symbol" => ProtocolOperations.SearchSymbol,
                "search_text" => ProtocolOperations.SearchText,
                "read_file" => "READ_FILE",
                "read_method" => "READ_METHOD",
                "snapshot" => ProtocolOperations.GetFileSnapshot,
                "callers" => ProtocolOperations.GetCallers,
                "callees" => ProtocolOperations.GetCallees,
                "patch_preview" => "PATCH_PREVIEW",
                "patch_apply" => ProtocolOperations.Patch,
                "build" => ProtocolOperations.RunBuild,
                "test" => ProtocolOperations.RunTests,
                _ => throw new NotSupportedException($"Unknown tool: {call.Tool}")
            };

            var payload = BuildPayload(call.Tool, call.Args);
            var request = new ProtocolRequest("1.0", operation, session.Id.Value.ToString(), payload);
            return await _protocolEngine.ExecuteAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            return new ProtocolResponse(
                false,
                JsonSerializer.SerializeToElement(new { error = ex.Message }),
                [],
                [new ProtocolError("ToolExecutionError", $"Failed to execute {call.Tool}: {ex.Message}")]);
        }
    }

    private Task<ProtocolResponse> HandlePatchStatusAsync()
    {
        if (_sandboxRoot is null)
        {
            return Task.FromResult(new ProtocolResponse(
                true,
                JsonSerializer.SerializeToElement(new { message = "Not in sandbox mode." }),
                [],
                []));
        }

        var changedFiles = new List<string>();
        foreach (var sandboxFile in EnumerateFiles(_sandboxRoot))
        {
            var relativePath = Path.GetRelativePath(_sandboxRoot, sandboxFile);
            changedFiles.Add(relativePath);
        }

        var result = new
        {
            changedFiles = changedFiles,
            message = changedFiles.Count > 0 ? $"{changedFiles.Count} files changed." : "No files changed."
        };

        return Task.FromResult(new ProtocolResponse(
            true,
            JsonSerializer.SerializeToElement(result),
            [],
            []));
    }

    private static IEnumerable<string> EnumerateFiles(string root)
    {
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".ecp", "bin", "obj", ".vs", ".idea", "node_modules"
        };

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var parts = file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Any(p => ignored.Contains(p))) continue;
            yield return file;
        }
    }

    private static JsonElement BuildPayload(string tool, JsonElement args)
    {
        if (tool == "patch_apply" || tool == "patch_preview")
        {
            return args;
        }

        var builder = new System.Text.Json.Nodes.JsonObject();
        foreach (var prop in args.EnumerateObject())
        {
            builder[prop.Name] = System.Text.Json.Nodes.JsonNode.Parse(prop.Value.GetRawText());
        }

        switch (tool)
        {
            case "search_symbol":
                if (!builder.ContainsKey("query") && builder.ContainsKey("symbol"))
                {
                    builder["query"] = builder["symbol"];
                    builder.Remove("symbol");
                }
                break;
            case "read_file":
            case "snapshot":
                if (!builder.ContainsKey("query") && builder.ContainsKey("path"))
                {
                    builder["query"] = builder["path"];
                    builder.Remove("path");
                }
                break;
        }

        return JsonSerializer.SerializeToElement(builder);
    }

    private static string? SafeGetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }
        return null;
    }

    private sealed record ConversationMessage(string Role, string Content);
    private sealed record ToolCall(string Tool, JsonElement Args);
}
