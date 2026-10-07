using DevX.Cli;
using Xunit;

namespace DevX.Tests.Cli;

public sealed class LlmErrorRedactionTests
{
    [Fact]
    public void FailureMessage_DoesNotIncludeResponseBody()
    {
        const string apiKey = "sk-example-secret";
        var message = LlmErrorRedaction.SafeFailureMessage(401);

        Assert.Contains("401", message);
        Assert.DoesNotContain(apiKey, message);
        Assert.DoesNotContain("response body", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SafeEndpoint_RemovesCredentialsQueryAndFragment()
    {
        var endpoint = LlmErrorRedaction.SafeEndpoint("https://user:sk-example-secret@llm.example.com/v1?api_key=sk-example-secret#token");

        Assert.Equal("https://llm.example.com/v1", endpoint);
        Assert.DoesNotContain("sk-example-secret", endpoint);
    }
}
