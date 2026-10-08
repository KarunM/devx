using DevX.Indexer;
using Xunit;

namespace DevX.Tests.Indexer;

public sealed class DotnetBuildTestServiceTests
{
    [Fact]
    public void BuildCSharpFileTestCommand_UsesNearestProjectAndClassFilter()
    {
        var root = Directory.CreateTempSubdirectory("devx-test-command").FullName;
        try
        {
            var projectDirectory = Directory.CreateDirectory(Path.Combine(root, "tests"));
            File.WriteAllText(Path.Combine(projectDirectory.FullName, "Tests.csproj"), "");
            var source = Path.Combine(projectDirectory.FullName, "SampleTests.cs");
            File.WriteAllText(source, "[Fact] public class SampleTests { }");

            var command = DotnetBuildTestService.BuildCSharpFileTestCommand(root, source);

            Assert.Equal("dotnet", command.FileName);
            Assert.Contains("test", command.Arguments);
            Assert.Contains("Tests.csproj", command.Arguments);
            Assert.Contains("FullyQualifiedName~SampleTests", command.Arguments);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BuildCSharpDirectoryTestCommand_UsesEnclosingProjectAndDiscoveredClassFilter()
    {
        var root = Directory.CreateTempSubdirectory("devx-directory-test-command").FullName;
        try
        {
            var project = Directory.CreateDirectory(Path.Combine(root, "tests", "Widget.Tests"));
            File.WriteAllText(Path.Combine(project.FullName, "Widget.Tests.csproj"), "");
            var selected = Directory.CreateDirectory(Path.Combine(project.FullName, "Unit"));
            File.WriteAllText(Path.Combine(selected.FullName, "WidgetTests.cs"), "public class WidgetTests { }");
            var command = DotnetBuildTestService.BuildCSharpDirectoryTestCommand(root, selected.FullName);
            Assert.NotNull(command);
            Assert.Equal("dotnet", command!.Value.FileName);
            Assert.Contains("Widget.Tests.csproj", command.Value.Arguments);
            Assert.Contains("FullyQualifiedName~WidgetTests", command.Value.Arguments);
            Assert.DoesNotContain("PYTEST", command.Value.Arguments, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void AddTestFilter_PassesFilterThroughToDotnetTest()
    {
        Assert.Equal("test \"tests.csproj\" --filter \"FullyQualifiedName~WidgetTests\"",
            DotnetBuildTestService.AddTestFilter("test \"tests.csproj\"", "FullyQualifiedName~WidgetTests"));
    }

    [Fact]
    public void FormatRunnerSummary_ReportsNoSummaryWithoutChangingSuccessSemantics()
    {
        var summary = DotnetBuildTestService.FormatRunnerSummary("", "test", TimeSpan.Zero);
        Assert.Contains("no test summary was produced", summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<none>", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void FilteredDotnetRun_ZeroMatchesReturnsFailure()
    {
        const string filter = "NoSuchTestAtAll";
        Assert.True(DotnetBuildTestService.ReportsZeroDotnetTests("No test matches the given testcase filter."));
        Assert.True(DotnetBuildTestService.ReportsZeroDotnetTests("Passed! - Failed: 0, Passed: 0, Skipped: 0, Total: 0"));

        var result = DotnetBuildTestService.CreateFilteredNoTestsRecord("dotnet test --filter", filter, "/tmp/dotnet.log");

        Assert.False(result.Success);
        Assert.Equal($"FAIL tests — no tests matched the filter '{filter}' (0 tests ran)", result.Summary);
        Assert.Equal("DOTNET_FILTER", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void FormatNoTestSummary_UsesLastFiveNonemptyLines()
    {
        var output = "restore 1\n\nrestore 2\nrestore 3\nrestore 4\nrestore 5\nlast 1\nlast 2\nlast 3\nlast 4\nlast 5";

        var summary = DotnetBuildTestService.FormatNoTestSummary(output, 0);

        Assert.Equal("no test summary was produced (exit code 0). Last output: last 1 | last 2 | last 3 | last 4 | last 5", summary);
    }

    [Fact]
    public void FormatRunnerSummary_ParsesDotnetAndPytestSummaries()
    {
        var dotnet = DotnetBuildTestService.FormatRunnerSummary("Passed! - Failed: 0, Passed: 3, Skipped: 0, Total: 3", "test", TimeSpan.Zero);
        var pytest = DotnetBuildTestService.FormatRunnerSummary("3 passed in 0.12s", "test", TimeSpan.Zero);
        Assert.Equal("PASS 3 tests", dotnet);
        Assert.Equal("PASS 3 tests in 0.12s", pytest);
        Assert.DoesNotContain("PYTEST", dotnet, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPythonFileTestCommand_RunsPytestOnSelectedFile()
    {
        var root = Directory.CreateTempSubdirectory("devx-python-test-command").FullName;
        try
        {
            var file = Path.Combine(root, "test_sample.py");
            File.WriteAllText(file, "def test_example(): pass");

            var command = DotnetBuildTestService.BuildPythonFileTestCommand(root, file);

            Assert.Equal("python3", command.FileName);
            Assert.Contains("-m pytest", command.Arguments);
            Assert.Contains(file, command.Arguments);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
