using System.Reflection;
using DevX.Cli;
using Xunit;

namespace DevX.Tests.Cli;

[Collection("ProcessGlobalState")]
public sealed class ReplaceCommandTests
{
    private static readonly SemaphoreSlim StateLock = new(1, 1);

    [Fact] public async Task Replace_UniqueMatch_ReturnsZeroAndChangesFile() => await RunCase("one", ["--old", "one", "--new", "two"], 0, "two");
    [Fact] public async Task Replace_AmbiguousMatch_ReturnsOne() => await RunCase("one one", ["--old", "one", "--new", "two"], 1, "one one", ambiguous: true);
    [Fact] public async Task Replace_MissingMatch_ReturnsOne() => await RunCase("stable", ["--old", "missing", "--new", "two"], 1, "stable", missing: true);
    [Fact] public async Task Replace_All_ReplacesEveryOccurrence() => await RunCase("one one", ["--old", "one", "--new", "two", "--all"], 0, "two two");
    [Fact] public async Task Replace_Occurrence_ReplacesSelectedOccurrence() => await RunCase("one one", ["--old", "one", "--new", "two", "--occurrence", "2"], 0, "one two");

    private static async Task RunCase(string original, string[] options, int expected, string expectedContent, bool ambiguous = false, bool missing = false)
    {
        await StateLock.WaitAsync(); var oldDirectory = Environment.CurrentDirectory; var oldOut = Console.Out; var oldError = Console.Error;
        var root = Directory.CreateTempSubdirectory("devx-cli-replace").FullName; var file = Path.Combine(root, "sample.txt"); File.WriteAllText(file, original);
        try
        {
            Environment.CurrentDirectory = root; var output = new StringWriter(); Console.SetOut(output); Console.SetError(new StringWriter());
            var entry = typeof(ArtifactCommand).Assembly.EntryPoint!;
            var call = entry.Invoke(null, [new[] { "replace", "sample.txt" }.Concat(options).ToArray()]);
            var exit = call is Task<int> task ? await task : Convert.ToInt32(call);
            Assert.Equal(expected, exit); Assert.Equal(expectedContent, File.ReadAllText(file));
            if (ambiguous) Assert.Contains("2 times", output.ToString());
            if (missing) Assert.Equal(original, File.ReadAllText(file));
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); Environment.CurrentDirectory = oldDirectory; Directory.Delete(root, true); StateLock.Release(); }
    }
}
