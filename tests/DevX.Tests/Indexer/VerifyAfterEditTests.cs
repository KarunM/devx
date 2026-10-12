using DevX.Domain;
using DevX.Indexer;
using Xunit;

namespace DevX.Tests.Indexer;

public sealed class VerifyAfterEditTests
{
    [Fact]
    public async Task RunAsync_BuildPassRunsTestsAndFormatsLinesInOrder()
    {
        var testsCalled = false;
        var verifier = new VerifyAfterEdit(_ => Task.FromResult(Record(true, "PASS build")), files =>
        {
            testsCalled = true;
            return Task.FromResult(Record(true, "PASS 2 tests"));
        }, _ => ["tests/test_file.py"]);
        var result = await verifier.RunAsync("src/file.py");
        var lines = VerifyAfterEdit.FormatLines("replaced", "diff", result);
        Assert.True(testsCalled);
        Assert.True(result.Build.Success);
        Assert.Contains("build: PASS build", lines[2]);
        Assert.Contains("tests: PASS 2 tests", lines[3]);
        Assert.True(Array.IndexOf(lines.ToArray(), "build: PASS build") < Array.IndexOf(lines.ToArray(), "tests: PASS 2 tests"));
    }

    [Fact]
    public async Task RunAsync_BuildFailureSkipsTestsAndKeepsEditWithErrors()
    {
        var testsCalled = false;
        var verifier = new VerifyAfterEdit(_ => Task.FromResult(Record(false, "FAIL build — 1 errors\nsource.cs:3: CS1 error")), _ =>
        {
            testsCalled = true;
            return Task.FromResult(Record(true, "PASS tests"));
        }, _ => []);
        var result = await verifier.RunAsync("source.cs");
        var lines = VerifyAfterEdit.FormatLines("replaced", "", result);
        Assert.False(testsCalled);
        Assert.Contains("source.cs:3", result.Build.Summary);
        Assert.Contains("edit kept; fix forward or revert", lines);
    }

    [Fact]
    public async Task RunAsync_NoRelevantTestsInvokesDefaultTestCommand()
    {
        IReadOnlyList<string>? selected = null;
        var verifier = new VerifyAfterEdit(_ => Task.FromResult(Record(true, "PASS build")), files =>
        {
            selected = files;
            return Task.FromResult(Record(true, "PASS tests"));
        }, _ => []);
        await verifier.RunAsync("file.cs");
        Assert.NotNull(selected);
        Assert.Empty(selected);
    }

    [Fact]
    public void SkippedBuild_MarkdownHasNoBuildStep()
    {
        Assert.False(VerifyAfterEdit.HasBuildStep("README.md"));
        Assert.Contains("skipped (no build step for .md)", VerifyAfterEdit.SkippedBuild("README.md").Summary);
    }

    [Fact]
    public void FindRelevantTestFiles_SelectsPythonAndCSharpTestsContainingChangedSymbols()
    {
        var root = Directory.CreateTempSubdirectory("devx-verify-discovery").FullName;
        try
        {
            System.IO.File.WriteAllText(Path.Combine(root, "module.py"), "def useful_symbol():\n    pass\n");
            Directory.CreateDirectory(Path.Combine(root, "tests"));
            System.IO.File.WriteAllText(Path.Combine(root, "tests", "test_module.py"), "def test_useful_symbol():\n    useful_symbol()\n");
            System.IO.File.WriteAllText(Path.Combine(root, "tests", "ModuleTests.cs"), "public class ModuleTests { void useful_symbol() {} }");
            System.IO.File.WriteAllText(Path.Combine(root, "tests", "test_other.py"), "def unrelated(): pass\n");
            var files = VerifyAfterEdit.FindRelevantTestFiles(root, "module.py");
            Assert.Contains(files, path => path.EndsWith("test_module.py", StringComparison.Ordinal));
            Assert.Contains(files, path => path.EndsWith("ModuleTests.cs", StringComparison.Ordinal));
            Assert.DoesNotContain(files, path => path.EndsWith("test_other.py", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void IsTestFile_ExcludesSupportModulesUnderATestDirectory()
    {
        var root = Directory.CreateTempSubdirectory("devx-testfile").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "tests"));
            // pytest on a conftest collects nothing and exits 5, so selecting one turns a passing
            // verify into a false failure.
            Assert.False(VerifyAfterEdit.IsTestFile(root, Path.Combine(root, "tests", "conftest.py")));
            Assert.False(VerifyAfterEdit.IsTestFile(root, Path.Combine(root, "tests", "__init__.py")));
            Assert.True(VerifyAfterEdit.IsTestFile(root, Path.Combine(root, "tests", "test_thing.py")));
            Assert.True(VerifyAfterEdit.IsTestFile(root, Path.Combine(root, "tests", "ThingTests.cs")));
            Assert.False(VerifyAfterEdit.IsTestFile(root, Path.Combine(root, "src", "thing.py")));
        }
        finally { Directory.Delete(root, true); }
    }

    private static ExecutionRecord Record(bool success, string summary) => new("test", success, "", [], summary);

    [Theory]
    [InlineData("build", VerifyMode.Build)]
    [InlineData("BUILD", VerifyMode.Build)]
    [InlineData("test", VerifyMode.Test)]
    [InlineData("tests", VerifyMode.Test)]
    [InlineData("both", VerifyMode.Both)]
    [InlineData(null, VerifyMode.Both)]
    [InlineData("frobnicate", VerifyMode.Both)]
    public void ParseMode_ReachesTheModeOrFallsBackToBoth(string? value, VerifyMode expected)
        => Assert.Equal(expected, VerifyAfterEdit.ParseMode(value));

    [Fact]
    public void ParseVerifyFlag_IsOptInAndTheLastFlagWins()
    {
        Assert.Null(VerifyAfterEdit.ParseVerifyFlag(["replace", "file.cs", "--old", "a", "--new", "b"]));
        Assert.Equal(VerifyMode.Both, VerifyAfterEdit.ParseVerifyFlag(["replace", "--verify"]));
        Assert.Equal(VerifyMode.Build, VerifyAfterEdit.ParseVerifyFlag(["replace", "--verify=build"]));
        Assert.Equal(VerifyMode.Test, VerifyAfterEdit.ParseVerifyFlag(["replace", "--verify=tests"]));
        Assert.Null(VerifyAfterEdit.ParseVerifyFlag(["replace", "--verify=build", "--no-verify"]));
        Assert.Equal(VerifyMode.Both, VerifyAfterEdit.ParseVerifyFlag(["replace", "--no-verify", "--verify"]));
        // `--verify` is not a prefix match for anything else on the line.
        Assert.Null(VerifyAfterEdit.ParseVerifyFlag(["replace", "--verify-later"]));
    }

    [Fact]
    public async Task RunAsync_TestMode_SkipsTheBuildAndStillRunsTheTests()
    {
        var buildCalled = false;
        var verifier = new VerifyAfterEdit(_ => { buildCalled = true; return Task.FromResult(Record(true, "PASS build")); },
            _ => Task.FromResult(Record(true, "PASS 2 tests")), _ => ["tests/test_file.py"]);

        var result = await verifier.RunAsync("src/file.py", VerifyMode.Test);

        Assert.False(buildCalled);
        Assert.True(result.BuildSkipped);
        Assert.Contains("skipped (--verify=test)", result.Build.Summary);
        Assert.NotNull(result.Tests);
        Assert.Equal("PASS edit — PASS 2 tests", VerifyAfterEdit.FormatOneLine("edit", result));
    }

    [Fact]
    public async Task RunAsync_BuildMode_LeavesTheTestsUnrun()
    {
        var testsCalled = false;
        var verifier = new VerifyAfterEdit(_ => Task.FromResult(Record(true, "PASS build in 1.0s (0 warnings) log: .devx/execution/1.log")),
            _ => { testsCalled = true; return Task.FromResult(Record(true, "PASS tests")); }, _ => ["tests/test_file.py"]);

        var result = await verifier.RunAsync("src/file.cs", VerifyMode.Build);

        Assert.False(testsCalled);
        Assert.Null(result.Tests);
        // One line: the log path is printed once, not once per run.
        Assert.Equal("PASS edit — PASS build in 1.0s (0 warnings) — log .devx/execution/1.log", VerifyAfterEdit.FormatOneLine("edit", result));
    }

    [Fact]
    public async Task FormatVerifyLines_KeepsTheDiagnosticLinesAndSaysWhatDidNotRun()
    {
        var verifier = new VerifyAfterEdit(_ => Task.FromResult(Record(false, "FAIL build — 1 errors (0 warnings)\nsource.cs:3: CS1 error\nlog: .devx/execution/2.log")),
            _ => Task.FromResult(Record(true, "PASS tests")), _ => []);
        var result = await verifier.RunAsync("source.cs");

        var lines = VerifyAfterEdit.FormatVerifyLines(result);
        Assert.Contains("FAIL build — 1 errors (0 warnings)", lines);
        Assert.Contains("source.cs:3: CS1 error", lines);
        Assert.Contains("log: .devx/execution/2.log", lines);
        Assert.Contains("tests: skipped (build failed)", lines);
    }
}
