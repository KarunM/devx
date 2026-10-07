using DevX.Cli;
using Xunit;

namespace DevX.Tests;

[Collection("ProcessGlobalState")]
public sealed class ArtifactCommandTests
{
    [Fact]
    public async Task CreatePathAndDelete_RoundTripArtifactFiles()
    {
        var originalWorkingDirectory = Environment.CurrentDirectory;
        var tempDirectory = Directory.CreateTempSubdirectory("devx-artifact-test").FullName;
        Environment.CurrentDirectory = tempDirectory;

        try
        {
            var createResult = await ArtifactCommand.RunAsync(["create", "demo", "--content", "hello-from-devx"], "text");
            Assert.Equal(0, createResult);

            var pathResult = await ArtifactCommand.RunAsync(["path", "demo"], "text");
            Assert.Equal(0, pathResult);

            var artifactPath = Path.Combine(tempDirectory, ".devx", "artifacts", "demo");
            Assert.True(File.Exists(artifactPath));
            Assert.Equal("hello-from-devx", await File.ReadAllTextAsync(artifactPath));

            var deleteResult = await ArtifactCommand.RunAsync(["delete", "demo"], "text");
            Assert.Equal(0, deleteResult);
            Assert.False(File.Exists(artifactPath));
        }
        finally
        {
            Environment.CurrentDirectory = originalWorkingDirectory;
        }
    }

    [Fact]
    public async Task Create_WithContentJson_WritesDecodedString()
    {
        var originalWorkingDirectory = Environment.CurrentDirectory;
        var tempDirectory = Directory.CreateTempSubdirectory("devx-artifact-json-test").FullName;
        Environment.CurrentDirectory = tempDirectory;

        try
        {
            var createResult = await ArtifactCommand.RunAsync(["create", "json-demo", "--content-json", "\"hello\\nworld\""], "text");
            Assert.Equal(0, createResult);

            var artifactPath = Path.Combine(tempDirectory, ".devx", "artifacts", "json-demo");
            Assert.Equal("hello\nworld", await File.ReadAllTextAsync(artifactPath));
        }
        finally
        {
            Environment.CurrentDirectory = originalWorkingDirectory;
        }
    }
}
