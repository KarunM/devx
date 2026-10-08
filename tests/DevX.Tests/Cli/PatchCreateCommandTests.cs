using System.Text.Json;
using DevX.Cli;
using Xunit;

namespace DevX.Tests.Cli;

public sealed class PatchCreateCommandTests
{
    [Theory]
    [InlineData("--all", null, true)]
    [InlineData("--occurrence", "2", false)]
    public async Task RunAsync_StoresTextReplaceSelectionOptions(string mode, string? value, bool replaceAll)
    {
        var root = Directory.CreateTempSubdirectory("devx-patch-create-text").FullName;
        try
        {
            var output = Path.Combine(root, "replace.patch.json");
            var args = new List<string> { "patch", "create", output, "--session", Guid.NewGuid().ToString(), "--op", "ReplaceText", "--path", "Sample.txt", "--old-text", "x", "--replacement", "y" };
            if (replaceAll) args.Add(mode);
            else { args.Add(mode); args.Add(value!); }
            var result = await PatchCreateCommand.RunAsync($"Data Source={Path.Combine(root, "state.db")}", args.ToArray(), "json");
            Assert.Equal(0, result);
            using var artifact = JsonDocument.Parse(await File.ReadAllTextAsync(output));
            var operation = artifact.RootElement.GetProperty("operations")[0];
            Assert.Equal("ReplaceText", operation.GetProperty("operation").GetString());
            Assert.Equal("x", operation.GetProperty("oldText").GetString());
            Assert.Equal("y", operation.GetProperty("replacementText").GetString());
            if (replaceAll) Assert.True(operation.GetProperty("replaceAll").GetBoolean());
            else Assert.Equal(2, operation.GetProperty("occurrence").GetInt32());
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
