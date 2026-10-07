using DevX.Cli;
using Xunit;

namespace DevX.Tests.Cli;

public sealed class LlmMessageFormatterTests
{
    [Fact]
    public void ParseTaggedPrompt_PreservesMultiTurnConversation()
    {
        var prompt = """
            <system>
            You are a coding agent.
            </system>

            <user>
            Fix the test
            </user>

            <assistant>
            <tool_call>
            {"name": "search_symbol", "arguments": {"query": "Foo"}}
            </tool_call>
            </assistant>

            <tool>
            {"results": []}
            </tool>
            """;

        var messages = LlmMessageFormatter.ParseTaggedPrompt(prompt);

        Assert.Equal(4, messages.Count);
        Assert.Equal("system", messages[0].Role);
        Assert.Equal("user", messages[1].Role);
        Assert.Equal("assistant", messages[2].Role);
        Assert.Equal("user", messages[3].Role);
        Assert.StartsWith("Tool result:", messages[3].Content);
    }

    [Fact]
    public void FormatToolCalls_ConvertsOpenAiToolCallsToDevXFormat()
    {
        var toolCalls = new List<OpenAiToolCall>
        {
            new(new OpenAiFunction("search_symbol", """{"query":"Foo"}"""))
        };

        var formatted = LlmMessageFormatter.FormatToolCalls(toolCalls);

        Assert.Contains("<tool_call>", formatted);
        Assert.Contains("\"name\":\"search_symbol\"", formatted);
        Assert.Contains("\"query\":\"Foo\"", formatted);
    }

    [Fact]
    public void StripThinking_RemovesQwen3ThinkingBlock()
    {
        var text = """
            <think>
            plan the next step
            </think>
            <tool_call>
            {"name": "done", "arguments": {"summary": "ok"}}
            </tool_call>
            """;

        var stripped = LlmMessageFormatter.StripThinking(text);

        Assert.DoesNotContain("plan the next step", stripped);
        Assert.Contains("<tool_call>", stripped);
    }
}
