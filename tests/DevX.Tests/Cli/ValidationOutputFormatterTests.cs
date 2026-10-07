using System.Text.Json;
using DevX.Application;
using DevX.Cli;
using DevX.Domain;
using DevX.Protocol;
using DevX.Indexer;
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

    [Fact]
    public void Format_DotnetTestFailureDoesNotUsePytestLabel()
    {
        var diagnostic = Assert.Single(DotnetBuildTestService.ParseDiagnostics("Failed! - Failed: 1, Passed: 0, Skipped: 0, Total: 1", "/tmp/dotnet.log"));
        var text = ValidationOutputFormatter.Format(Response(false, new ExecutionRecord("dotnet test", false, "/tmp/dotnet.log", [diagnostic])), "test");

        Assert.DoesNotContain("PYTEST", text, StringComparison.Ordinal);
        Assert.Contains("DOTNET_TEST", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_PytestFailureKeepsPytestLabel()
    {
        var diagnostic = Assert.Single(DotnetBuildTestService.ParseDiagnostics("tests/test_sample.py:3: AssertionError", "/tmp/pytest.log"));
        var text = ValidationOutputFormatter.Format(Response(false, new ExecutionRecord("-m pytest", false, "/tmp/pytest.log", [diagnostic])), "test");

        Assert.Contains("PYTEST", text, StringComparison.Ordinal);
    }

    private static ProtocolResponse Response(bool success, ExecutionRecord execution)
    {
        var context = new RuntimeContext("/repo", "/repo", ".", "python", "default");
        var target = new RuntimeTargetDescriptor("python", "repo", ".", "Healthy", []);
        var payload = new ValidationExecutionResponse("test", context, target, execution);
        return new ProtocolResponse(success, JsonSerializer.SerializeToElement(payload), [], success ? [] : [new ProtocolError("ValidationError", "Tests failed.")]);
    }
}
