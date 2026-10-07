using DevX.Indexer;
using Xunit;

namespace DevX.Tests.Indexer;

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
}
