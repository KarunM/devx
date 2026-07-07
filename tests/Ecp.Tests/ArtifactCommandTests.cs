using Ecp.Cli;
using Xunit;

namespace Ecp.Tests;

public sealed class ArtifactCommandTests
{
    [Fact]
    public async Task CreatePathAndDelete_RoundTripArtifactFiles()
    {
        var originalWorkingDirectory = Environment.CurrentDirectory;
        var tempDirectory = Directory.CreateTempSubdirectory("ecp-artifact-test").FullName;
        Environment.CurrentDirectory = tempDirectory;

        try
        {
            var createResult = await ArtifactCommand.RunAsync(["create", "demo", "--content", "hello-from-ecp"], "text");
            Assert.Equal(0, createResult);

            var pathResult = await ArtifactCommand.RunAsync(["path", "demo"], "text");
            Assert.Equal(0, pathResult);

            var artifactPath = Path.Combine(tempDirectory, ".ecp", "artifacts", "demo");
            Assert.True(File.Exists(artifactPath));
            Assert.Equal("hello-from-ecp", await File.ReadAllTextAsync(artifactPath));

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
        var tempDirectory = Directory.CreateTempSubdirectory("ecp-artifact-json-test").FullName;
        Environment.CurrentDirectory = tempDirectory;

        try
        {
            var createResult = await ArtifactCommand.RunAsync(["create", "json-demo", "--content-json", "\"hello\\nworld\""], "text");
            Assert.Equal(0, createResult);

            var artifactPath = Path.Combine(tempDirectory, ".ecp", "artifacts", "json-demo");
            Assert.Equal("hello\nworld", await File.ReadAllTextAsync(artifactPath));
        }
        finally
        {
            Environment.CurrentDirectory = originalWorkingDirectory;
        }
    }
}
