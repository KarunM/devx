using DevX.Application;
using DevX.Domain;
using DevX.Indexer;
using Xunit;
using File = System.IO.File;

namespace DevX.Tests.Indexer;

/// <summary>
/// Item 4 of OSS10: build and test have to work on a repository that is not a .NET solution.
/// The LG1 transcript got "No runtime target matched" and then a bare "Build failed." out of
/// its `test` and `build` calls, fell back to Bash, ran pytest against Django (`0 failed of
/// 0`), and installed pytest into the task venv to make the tool agree with it.
/// </summary>
[Collection("ProcessGlobalState")]
public sealed class ConfiguredCommandTests
{
    private static string NewRoot(string prefix) => Directory.CreateTempSubdirectory(prefix).FullName;

    private static async Task WriteConfigAsync(string root, string section, string block)
    {
        Directory.CreateDirectory(Path.Combine(root, ".devx"));
        await File.WriteAllTextAsync(Path.Combine(root, ".devx", "config.json"), $"{{ \"{section}\": {block} }}");
    }

    [Fact]
    public async Task ConfiguredTestCommand_IsUsedInsteadOfTheDiscoveredRunner()
    {
        var root = NewRoot("devx-configured-test");
        await File.WriteAllTextAsync(Path.Combine(root, "pyproject.toml"), "[tool.pytest.ini_options]\n");
        await WriteConfigAsync(root, "test", """{ "command": "python3", "args": ["-c", "print('CONFIGURED_OK')"] }""");

        var result = await new DotnetBuildTestService().RunTestsAsync(root, null);

        Assert.True(result.Success, result.Summary);
        Assert.Contains("CONFIGURED_OK", result.Command);
        Assert.DoesNotContain("-m pytest", result.Command);
    }

    [Fact]
    public async Task ConfiguredTestCommand_ReceivesTheTargetLabel()
    {
        var root = NewRoot("devx-configured-target");
        await WriteConfigAsync(root, "test", """{ "command": "python3", "args": ["-c", "import sys; print(sys.argv[1:])", "{target}"] }""");

        var result = await new DotnetBuildTestService().RunTestsAsync(
            root,
            ["queries.tests.QueriesTests"],
            TimeSpan.FromSeconds(30));

        Assert.True(result.Success, result.Summary);
        Assert.Contains("queries.tests.QueriesTests", result.Command);
    }

    [Fact]
    public async Task ShellStyleConfiguredCommand_IsSupported()
    {
        var root = NewRoot("devx-configured-shell");
        await File.WriteAllTextAsync(Path.Combine(root, "runner.py"), "print('SHELL_OK')\n");
        await WriteConfigAsync(root, "test", """{ "command": "cd . && python3 runner.py" }""");

        var result = await new DotnetBuildTestService().RunTestsAsync(root, null);

        Assert.True(result.Success, result.Summary);
        Assert.Contains("SHELL_OK", File.ReadAllText(result.RawOutput));
    }

    [Fact]
    public async Task DjangoSuite_IsRunThroughRuntestsWithTheLabel()
    {
        var root = NewRoot("devx-django");
        Directory.CreateDirectory(Path.Combine(root, "tests"));
        await File.WriteAllTextAsync(Path.Combine(root, "tests", "runtests.py"), "import sys\nprint('RUNTESTS', sys.argv[1:])\n");
        await File.WriteAllTextAsync(Path.Combine(root, "pyproject.toml"), "[project]\nname = \"django\"\nversion = \"0.0.0\"\n");

        var result = await new DotnetBuildTestService().RunTestsAsync(
            root,
            ["queries.tests.QueriesTests"],
            TimeSpan.FromSeconds(60));

        Assert.True(result.Success, result.Summary);
        Assert.Contains("runtests.py", result.Command);
        Assert.Contains("queries.tests.QueriesTests", result.Command);
        Assert.DoesNotContain("dotnet test", result.Command);
    }

    [Fact]
    public async Task ConfiguredInstallCommand_IsRefused()
    {
        var root = NewRoot("devx-no-install");
        await WriteConfigAsync(root, "test", """{ "command": "python3", "args": ["-m", "pip", "install", "pytest"] }""");

        var result = await new DotnetBuildTestService().RunTestsAsync(root, null);

        Assert.False(result.Success);
        Assert.Contains("refused a package install", result.Summary);
        Assert.Contains("PACKAGE_INSTALL", result.Diagnostics.Select(diagnostic => diagnostic.Code));
    }

    [Theory]
    [InlineData("python3 -m pip install pytest")]
    [InlineData("npm install")]
    [InlineData("dotnet restore")]
    [InlineData("cd tests && .venv/bin/pip install -r requirements.txt")]
    public void PackageInstalls_AreRecognised(string commandLine)
        => Assert.True(PackageInstallGuard.IsInstall(commandLine));

    [Theory]
    [InlineData("dotnet build DevX.sln")]
    [InlineData("dotnet test DevX.sln")]
    [InlineData("python3 tests/runtests.py queries.tests.QueriesTests")]
    [InlineData("./scripts/installer-check.sh")]
    public void OrdinaryCommands_AreNotMistakenForInstalls(string commandLine)
        => Assert.False(PackageInstallGuard.IsInstall(commandLine));

    [Fact]
    public async Task PythonBuildFailure_ReportsPathLineMessage()
    {
        var root = NewRoot("devx-build-error");
        await File.WriteAllTextAsync(Path.Combine(root, "broken.py"), "def broken(:\n    pass\n");

        var result = await new DotnetBuildTestService().RunBuildAsync(root);

        Assert.False(result.Success);
        Assert.Contains("broken.py:1:", result.Summary);
        Assert.Contains("SyntaxError", result.Summary);
    }

    [Fact]
    public void NoRuntimeTarget_NamesTheConfigKey()
    {
        var root = NewRoot("devx-no-target");

        var resolution = new RuntimeContextResolver().Resolve(root, null, null, ValidationMode.Test);

        Assert.False(resolution.Success);
        Assert.Contains(".devx/config.json", resolution.Errors.First());
    }
}
