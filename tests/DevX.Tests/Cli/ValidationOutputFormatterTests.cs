using System.Text.Json;
using DevX.Application;
using DevX.Cli;
using DevX.Domain;
using DevX.Protocol;
using Xunit;

namespace DevX.Tests.Cli;

public sealed class ValidationOutputFormatterTests
{
    [Fact]
    public void Format_PassingTests_ReturnsOnlyShortSummary()
    {
        var execution = new ExecutionRecord("-m pytest", true, string.Empty, [], "PASS 12 tests in 0.4s");
        var response = Response(true, execution);

        Assert.Equal("PASS 12 tests in 0.4s", ValidationOutputFormatter.Format(response, "test"));
    }

    [Fact]
    public void Format_FailingTests_ReturnsOneDiagnosticAndLogPath()
    {
        var diagnostic = new DiagnosticRecord("error", "PYTEST", "assert 1 == 2\nE assert false", "tests/test_app.py", 8, null, "/tmp/devx.log");
        var execution = new ExecutionRecord("-m pytest", false, "/tmp/devx.log", [diagnostic]);
        var response = Response(false, execution);

        Assert.Equal(
            $"FAIL tests — PYTEST: assert 1 == 2 E assert false (tests/test_app.py:8){Environment.NewLine}Log: /tmp/devx.log",
            ValidationOutputFormatter.Format(response, "test"));
    }

    private static ProtocolResponse Response(bool success, ExecutionRecord execution)
    {
        var context = new RuntimeContext("/repo", "/repo", ".", "python", "default");
        var target = new RuntimeTargetDescriptor("python", "repo", ".", "Healthy", []);
        var payload = new ValidationExecutionResponse("test", context, target, execution);
        return new ProtocolResponse(success, JsonSerializer.SerializeToElement(payload), [], success ? [] : [new ProtocolError("ValidationError", "Tests failed.")]);
    }
}
