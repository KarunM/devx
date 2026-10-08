using DevX.Indexer;
using Xunit;

namespace DevX.Tests.Indexer;

public sealed class RunSummaryFormatterTests
{
    [Fact]
    public void FormatBuild_SuccessCountsWarningsAndIncludesLog()
    {
        var output = "src/A.cs(4,2): warning CS0168: one warning\nsrc/B.cs(7,3): warning CS0219: another warning";
        var summary = RunSummaryFormatter.FormatBuild(output, true, TimeSpan.FromSeconds(1.2), "/repo/.devx/execution/run.log", "/repo");
        Assert.Contains("PASS build in", summary);
        Assert.Contains("(2 warnings)", summary);
        Assert.Contains("log: .devx/execution/run.log", summary);
        Assert.DoesNotContain("CS0168", summary);
    }

    [Fact]
    public void FormatBuild_FailureCapsDiagnosticsAtTenAndTrimsLongMessages()
    {
        var output = string.Join('\n', Enumerable.Range(1, 12).Select(index => $"src/File{index}.cs({index},1): error CS1000: {new string('x', 250)}"));
        var summary = RunSummaryFormatter.FormatBuild(output, false, TimeSpan.FromSeconds(1), "/repo/run.log", "/repo");
        Assert.Contains("FAIL build — 12 errors (0 warnings)", summary);
        Assert.Equal(10, summary.Split('\n').Count(line => line.Contains(": CS1000 ", StringComparison.Ordinal)));
        Assert.All(summary.Split('\n').Where(line => line.Contains(": CS1000 ", StringComparison.Ordinal)), line => Assert.True(line.Split(": CS1000 ", 2)[1].Length <= 200));
        Assert.Contains("log: run.log", summary);
    }

    [Fact]
    public void FormatBuild_DeduplicatesAbsoluteDiagnosticsAndRendersRelativePaths()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "devx-summary-root"));
        var source = Path.Combine(root, "src", "Program.cs");
        var project = Path.Combine(root, "src", "p.csproj");
        var diagnostic = $"{source}(1,1): error CS0029: Cannot implicitly convert type 'string' to 'int' [{project}]";
        var summary = RunSummaryFormatter.FormatBuild($"{diagnostic}\n{diagnostic}", false, TimeSpan.FromSeconds(1), Path.Combine(root, "run.log"), root);
        Assert.Contains("FAIL build — 1 errors (0 warnings)", summary);
        Assert.Equal(1, summary.Split('\n').Count(line => line.Contains("CS0029", StringComparison.Ordinal)));
        Assert.Contains("src/Program.cs:1: CS0029 Cannot implicitly convert type 'string' to 'int'", summary.Replace('\\', '/'));
        Assert.DoesNotContain(project, summary);
        Assert.Contains("log: run.log", summary);
    }

    [Fact]
    public void FormatTests_DotnetFailureIncludesCountsNamesAssertionsAndLog()
    {
        var output = "Failed: 2, Passed: 3, Skipped: 0, Total: 5\nFailed Sample.One [FAIL]\nError Message:\nAssert.Equal() Failure: expected 1, actual 2\nFailed Sample.Two [FAIL]\nError Message:\nAssert.True() Failure: false";
        var summary = RunSummaryFormatter.FormatTests(output, false, TimeSpan.FromSeconds(2), "/repo/run.log", "/repo");
        Assert.Contains("FAIL tests — 2 failed of 5 (0 warnings) in", summary);
        Assert.Contains("Sample.One: Assert.Equal() Failure", summary);
        Assert.Contains("Sample.Two: Assert.True() Failure", summary);
        Assert.Contains("log: run.log", summary);
    }

    [Fact]
    public void FormatTests_PytestSuccessCountsWarnings()
    {
        var summary = RunSummaryFormatter.FormatTests("2 passed, 3 warnings in 0.2s", true, TimeSpan.FromSeconds(.2), "/repo/run.log", "/repo");
        Assert.Contains("PASS 2 tests in", summary);
        Assert.Contains("(3 warnings)", summary);
        Assert.Contains("log: run.log", summary);
    }

    [Fact]
    public void FormatTests_PytestFailureIncludesFailingAssertion()
    {
        var output = "2 failed, 3 passed, 1 warning in 0.4s\ntests/test_a.py:4: AssertionError: assert 2 == 3\nFAILED tests/test_a.py::test_a - assert 2 == 3";
        var summary = RunSummaryFormatter.FormatTests(output, false, TimeSpan.FromSeconds(.4), "/repo/run.log", "/repo");
        Assert.Contains("FAIL tests — 2 failed of 5 (1 warnings) in", summary);
        Assert.Contains("tests/test_a.py::test_a: assert 2 == 3", summary);
        Assert.Contains("tests/test_a.py:4: PYTEST AssertionError", summary);
        Assert.Contains("log: run.log", summary);
    }

    [Fact]
    public void FormatTests_EmptyOutputSaysNoSummaryAndIncludesLog()
    {
        var summary = RunSummaryFormatter.FormatTests(string.Empty, true, TimeSpan.Zero, "/repo/run.log", "/repo");
        Assert.Contains("PASS tests — no test summary was produced", summary);
        Assert.Contains("log: run.log", summary);
    }
}
