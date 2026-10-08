using DevX.Indexer;
using Xunit;
using File = System.IO.File;

namespace DevX.Tests.Indexer;

[Collection("ProcessGlobalState")]
public sealed class BuildTestServiceTests
{
    [Fact]
    public async Task RunTestsAsync_RunsPytestAndPlainCheckScriptsTogether()
    {
        var root = Directory.CreateTempSubdirectory("devx-mixed-tests").FullName;
        await File.WriteAllTextAsync(Path.Combine(root, "test_unit.py"), "def test_ok():\n    assert True\n");
        await File.WriteAllTextAsync(Path.Combine(root, "beta_gate_034_test.py"), "print('15/15 checks passed')\n");

        var result = await new DotnetBuildTestService().RunTestsAsync(
            root,
            ["test_unit.py", "beta_gate_034_test.py"],
            TimeSpan.FromSeconds(30));

        Assert.True(result.Success);
        Assert.Contains("PASS 2/2 files", result.Summary);
        Assert.Contains("-m pytest", result.Command);
        Assert.Contains("beta_gate_034_test.py", result.Command);
    }

    [Fact]
    public async Task RunTestsAsync_PythonDirectoryInMixedRepositoryUsesPythonRunner()
    {
        var root = Directory.CreateTempSubdirectory("devx-python-directory").FullName;
        var pythonDirectory = Path.Combine(root, "python");
        Directory.CreateDirectory(pythonDirectory);
        await File.WriteAllTextAsync(Path.Combine(root, "solution.csproj"), "<Project />");
        await File.WriteAllTextAsync(Path.Combine(pythonDirectory, "pyproject.toml"), "[tool.pytest.ini_options]\n");
        await File.WriteAllTextAsync(Path.Combine(pythonDirectory, "test_smoke.py"), "def test_ok():\n    assert True\n");

        var result = await new DotnetBuildTestService().RunTestsAsync(
            root,
            ["python"],
            TimeSpan.FromSeconds(30));

        Assert.Contains("-m pytest", result.Command);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task RunTestsAsync_TerminatesHungScriptAtTimeout()
    {
        var root = Directory.CreateTempSubdirectory("devx-test-timeout").FullName;
        await File.WriteAllTextAsync(Path.Combine(root, "slow_check.py"), "import time\ntime.sleep(10)\n");

        var result = await new DotnetBuildTestService().RunTestsAsync(
            root,
            ["slow_check.py"],
            TimeSpan.FromMilliseconds(150));

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "TIMEOUT");
    }

    [Fact]
    public void IsStandaloneCheckScript_DetectsPlainScriptsAndConfiguredPaths()
    {
        var root = Directory.CreateTempSubdirectory("devx-check-detection").FullName;
        var scriptPath = Path.Combine(root, "check_gate.py");
        try
        {
            File.WriteAllText(scriptPath, "print('5/5 checks passed')\n");
            Assert.True(DotnetBuildTestService.IsStandaloneCheckScript(root, scriptPath));

            File.WriteAllText(scriptPath, "def test_collectable():\n    assert True\n");
            Assert.False(DotnetBuildTestService.IsStandaloneCheckScript(root, scriptPath));
            File.WriteAllText(scriptPath, "class TestCollectable:\n    pass\n");
            Assert.False(DotnetBuildTestService.IsStandaloneCheckScript(root, scriptPath));
            File.WriteAllText(scriptPath, "from pytest import fixture\n");
            Assert.False(DotnetBuildTestService.IsStandaloneCheckScript(root, scriptPath));

            Directory.CreateDirectory(Path.Combine(root, ".devx"));
            File.WriteAllText(Path.Combine(root, ".devx", "config.json"), "{\"testScriptPattern\": \"check_gate\\\\.py$\"}");
            Assert.True(DotnetBuildTestService.IsStandaloneCheckScript(root, scriptPath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void CreateProcessStartInfo_PreservesCallerEnvironment()
    {
        var original = Environment.GetEnvironmentVariable("DATABASE_URL");
        var root = Directory.CreateTempSubdirectory("devx-process-environment").FullName;
        try
        {
            Environment.SetEnvironmentVariable("DATABASE_URL", "devx-test-database-url");
            var info = DotnetBuildTestService.CreateProcessStartInfo("python3", "script.py", root);
            Assert.Equal("devx-test-database-url", info.Environment["DATABASE_URL"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DATABASE_URL", original);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunTestsAsync_StandaloneScriptInheritsDatabaseUrlAndWritesItToLog()
    {
        if (!Python3Available()) return;
        var root = Directory.CreateTempSubdirectory("devx-standalone-env").FullName;
        var scriptPath = Path.Combine(root, "check_env.py");
        var original = Environment.GetEnvironmentVariable("DATABASE_URL");
        const string databaseUrl = "devx-script-database-url";
        try
        {
            File.WriteAllText(scriptPath, "import os\nprint('startup noise')\nprint(os.environ.get('DATABASE_URL', 'missing'))\nprint('5/5 checks passed')\n");
            Environment.SetEnvironmentVariable("DATABASE_URL", databaseUrl);
            var result = await new DotnetBuildTestService().RunTestsAsync(root, [scriptPath], TimeSpan.FromSeconds(30));
            Assert.True(result.Success);
            Assert.Contains("PASS 5/5 checks", result.Summary);
            var executionDirectory = Path.Combine(root, ".devx", "execution");
            var logPath = Directory.GetFiles(executionDirectory, "*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .First();
            Assert.Contains(databaseUrl, await File.ReadAllTextAsync(Path.GetFullPath(logPath, root)));
            Assert.DoesNotContain(databaseUrl, result.Summary);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DATABASE_URL", original);
            Directory.Delete(root, recursive: true);
        }
    }

    private static bool Python3Available()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(directory => File.Exists(Path.Combine(directory, OperatingSystem.IsWindows() ? "python3.exe" : "python3")));
    }
}
