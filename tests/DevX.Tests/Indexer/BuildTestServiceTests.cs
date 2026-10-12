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
    public async Task RunTestsAsync_ForwardsPythonFilterAsSinglePytestArgument()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("devx-pytest-filter").FullName;
        var capture = Path.Combine(root, "captured-argv.txt");
        try
        {
            var testFile = Path.Combine(root, "test_unit.py");
            await File.WriteAllTextAsync(testFile, "def test_smoke(): pass\n");
            CreatePythonShim(root, capture, 0);

            var result = await new DotnetBuildTestService().RunTestsAsync(
                root, ["test_unit.py"], TimeSpan.FromSeconds(10), filter: "smoke and (fast or not slow)");

            Assert.True(result.Success, result.Summary);
            Assert.Equal(new[] { "-m", "pytest", testFile, "-k", "smoke and (fast or not slow)" }, await File.ReadAllLinesAsync(capture));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task RunTestsAsync_ForwardsPythonFilterToAutomaticRootDiscovery()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("devx-pytest-filter-root").FullName;
        var capture = Path.Combine(root, "captured-argv.txt");
        try
        {
            CreatePythonShim(root, capture, 0);
            var result = await new DotnetBuildTestService().RunTestsAsync(
                root, Array.Empty<string>(), TimeSpan.FromSeconds(10), filter: "smoke or fast");

            Assert.True(result.Success, result.Summary);
            Assert.Equal(new[] { "-m", "pytest", "-k", "smoke or fast" }, await File.ReadAllLinesAsync(capture));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task RunTestsAsync_ForwardsPythonFilterToDirectoryDiscovery()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("devx-pytest-filter-directory").FullName;
        var capture = Path.Combine(root, "captured-argv.txt");
        try
        {
            var testDirectory = Directory.CreateDirectory(Path.Combine(root, "tests"));
            await File.WriteAllTextAsync(Path.Combine(testDirectory.FullName, "test_unit.py"), "def test_smoke(): pass\n");
            CreatePythonShim(root, capture, 0);
            var result = await new DotnetBuildTestService().RunTestsAsync(
                root, ["tests"], TimeSpan.FromSeconds(10), filter: "smoke or fast");

            Assert.True(result.Success, result.Summary);
            Assert.Equal(new[] { "-m", "pytest", testDirectory.FullName, "-k", "smoke or fast" }, await File.ReadAllLinesAsync(capture));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("FullyQualifiedName~WidgetTests")]
    [InlineData("Name=WidgetTests")]
    [InlineData("smoke and")]
    [InlineData("(smoke or fast")]
    [InlineData("smoke; echo unsafe")]
    public async Task RunTestsAsync_RejectsUnsupportedPythonFilterBeforeStartingRunner(string filter)
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("devx-pytest-filter-reject").FullName;
        var capture = Path.Combine(root, "captured-argv.txt");
        try
        {
            var testFile = Path.Combine(root, "test_unit.py");
            await File.WriteAllTextAsync(testFile, "def test_smoke(): pass\n");
            CreatePythonShim(root, capture, 0);

            var result = await new DotnetBuildTestService().RunTestsAsync(
                root, ["test_unit.py"], TimeSpan.FromSeconds(10), filter: filter);

            Assert.False(result.Success);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PYTHON_FILTER_UNSUPPORTED");
            Assert.False(File.Exists(capture));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task RunTestsAsync_RejectsStandaloneCheckFilterBeforeStartingRunner()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("devx-check-filter-reject").FullName;
        var capture = Path.Combine(root, "captured-argv.txt");
        try
        {
            var checkFile = Path.Combine(root, "check_gate.py");
            await File.WriteAllTextAsync(checkFile, "print('5/5 checks passed')\n");
            CreatePythonShim(root, capture, 0);

            var result = await new DotnetBuildTestService().RunTestsAsync(
                root, ["check_gate.py"], TimeSpan.FromSeconds(10), filter: "smoke");

            Assert.False(result.Success);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PYTHON_FILTER_UNSUPPORTED");
            Assert.False(File.Exists(capture));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task RunTestsAsync_RejectsExcessivePytestFilterNestingBeforeStartingRunner()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("devx-pytest-filter-depth").FullName;
        var capture = Path.Combine(root, "captured-argv.txt");
        try
        {
            var testFile = Path.Combine(root, "test_unit.py");
            await File.WriteAllTextAsync(testFile, "def test_smoke(): pass\n");
            CreatePythonShim(root, capture, 0);

            var filter = new string('(', 65) + "smoke" + new string(')', 65);
            var result = await new DotnetBuildTestService().RunTestsAsync(
                root, ["test_unit.py"], TimeSpan.FromSeconds(10), filter: filter);

            Assert.False(result.Success);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PYTHON_FILTER_UNSUPPORTED");
            Assert.False(File.Exists(capture));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task RunTestsAsync_RejectsExcessivelyLongPytestFilterBeforeStartingRunner()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("devx-pytest-filter-length").FullName;
        var capture = Path.Combine(root, "captured-argv.txt");
        try
        {
            var testFile = Path.Combine(root, "test_unit.py");
            await File.WriteAllTextAsync(testFile, "def test_smoke(): pass\n");
            CreatePythonShim(root, capture, 0);

            var result = await new DotnetBuildTestService().RunTestsAsync(
                root, ["test_unit.py"], TimeSpan.FromSeconds(10), filter: new string('a', 4097));

            Assert.False(result.Success);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PYTHON_FILTER_UNSUPPORTED");
            Assert.False(File.Exists(capture));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task RunTestsAsync_RejectsConfiguredOpaqueRunnerFilterBeforeStartingRunner()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("devx-configured-runner-filter").FullName;
        var capture = Path.Combine(root, "captured-argv.txt");
        try
        {
            CreatePythonShim(root, capture, 0);
            var config = Directory.CreateDirectory(Path.Combine(root, ".devx"));
            await File.WriteAllTextAsync(Path.Combine(config.FullName, "config.json"),
                "{\"test\":{\"command\":\"custom-test-runner\",\"args\":[\"--suite\"]}}");

            var result = await new DotnetBuildTestService().RunTestsAsync(
                root, Array.Empty<string>(), TimeSpan.FromSeconds(10), filter: "smoke");

            Assert.False(result.Success);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PYTHON_FILTER_UNSUPPORTED");
            Assert.False(File.Exists(capture));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task RunTestsAsync_RejectsConfiguredShellRunnerFilterBeforeStartingRunner()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("devx-configured-shell-filter").FullName;
        var capture = Path.Combine(root, "captured-argv.txt");
        try
        {
            CreatePythonShim(root, capture, 0);
            var config = Directory.CreateDirectory(Path.Combine(root, ".devx"));
            await File.WriteAllTextAsync(Path.Combine(config.FullName, "config.json"),
                "{\"test\":{\"command\":\"python3 -m pytest && echo done\"}}");

            var result = await new DotnetBuildTestService().RunTestsAsync(
                root, Array.Empty<string>(), TimeSpan.FromSeconds(10), filter: "smoke");

            Assert.False(result.Success);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PYTHON_FILTER_UNSUPPORTED");
            Assert.False(File.Exists(capture));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunTestsAsync_RejectsDjangoLabelFilterBeforeStartingRunner(bool multipleLabels)
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("devx-django-filter-reject").FullName;
        var capture = Path.Combine(root, "captured-argv.txt");
        try
        {
            var djangoTests = Directory.CreateDirectory(Path.Combine(root, "tests"));
            await File.WriteAllTextAsync(Path.Combine(djangoTests.FullName, "runtests.py"), "print('suite')\n");
            CreatePythonShim(root, capture, 0);

            var result = await new DotnetBuildTestService().RunTestsAsync(
                root,
                multipleLabels ? ["queries.tests.QueriesTests", "auth.tests.AuthTests"] : ["queries.tests.QueriesTests"],
                TimeSpan.FromSeconds(10), filter: "smoke");

            Assert.False(result.Success);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PYTHON_FILTER_UNSUPPORTED");
            Assert.False(File.Exists(capture));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task RunTestsAsync_PytestExitCodeFiveRemainsFailureWithFilter()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("devx-pytest-filter-no-tests").FullName;
        var capture = Path.Combine(root, "captured-argv.txt");
        try
        {
            CreatePythonShim(root, capture, 5);
            var result = await new DotnetBuildTestService().RunTestsAsync(
                root, Array.Empty<string>(), TimeSpan.FromSeconds(10), filter: "no_such_test");

            Assert.False(result.Success);
            Assert.Equal(new[] { "-m", "pytest", "-k", "no_such_test" }, await File.ReadAllLinesAsync(capture));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task RunTestsAsync_ForwardsFilterToConfiguredDirectPytestRunner()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("devx-configured-pytest-filter").FullName;
        var capture = Path.Combine(root, "captured-argv.txt");
        try
        {
            var python = CreatePythonShim(root, capture, 0);
            await File.WriteAllTextAsync(Path.Combine(root, "test_configured.py"), "def test_smoke(): pass\n");
            var config = Directory.CreateDirectory(Path.Combine(root, ".devx"));
            await File.WriteAllTextAsync(Path.Combine(config.FullName, "config.json"),
                $"{{\"test\":{{\"command\":\"{python}\",\"args\":[\"-m\",\"pytest\",\"{{target}}\"]}}}}");

            var result = await new DotnetBuildTestService().RunTestsAsync(
                root, ["test_configured.py"], TimeSpan.FromSeconds(10), filter: "smoke or fast");

            Assert.True(result.Success, result.Summary);
            Assert.Equal(new[] { "-m", "pytest", "test_configured.py", "-k", "smoke or fast" }, await File.ReadAllLinesAsync(capture));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task RunTestsAsync_ForwardsFilterToConfiguredPytestExecutable()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("devx-configured-pytest-executable-filter").FullName;
        var capture = Path.Combine(root, "captured-argv.txt");
        try
        {
            var python = CreatePythonShim(root, capture, 0);
            var pytest = Path.Combine(Path.GetDirectoryName(python)!, "pytest");
            System.IO.File.Copy(python, pytest);
            if (!OperatingSystem.IsWindows())
                System.IO.File.SetUnixFileMode(pytest, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var config = Directory.CreateDirectory(Path.Combine(root, ".devx"));
            await File.WriteAllTextAsync(Path.Combine(config.FullName, "config.json"),
                $"{{\"test\":{{\"command\":\"{pytest}\"}}}}");

            var result = await new DotnetBuildTestService().RunTestsAsync(
                root, Array.Empty<string>(), TimeSpan.FromSeconds(10), filter: "smoke");

            Assert.True(result.Success, result.Summary);
            Assert.Equal(new[] { "-k", "smoke" }, await File.ReadAllLinesAsync(capture));
        }
        finally { Directory.Delete(root, recursive: true); }
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

    private static string CreatePythonShim(string root, string capturePath, int exitCode)
    {
        var executableDirectory = Directory.CreateDirectory(Path.Combine(root, ".venv", "bin"));
        var executable = Path.Combine(executableDirectory.FullName, "python");
        var quotedCapture = "'" + capturePath.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        var output = exitCode == 5 ? "no tests ran in 0.01s" : "1 passed in 0.01s";
        File.WriteAllText(executable,
            $"#!/bin/sh\nprintf '%s\\n' \"$@\" > {quotedCapture}\nprintf '{output}\\n'\nexit {exitCode}\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return executable;
    }
}
