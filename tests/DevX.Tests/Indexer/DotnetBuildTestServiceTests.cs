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
