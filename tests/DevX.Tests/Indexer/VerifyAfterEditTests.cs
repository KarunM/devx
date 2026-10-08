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

    private static ExecutionRecord Record(bool success, string summary) => new("test", success, "", [], summary);
}
