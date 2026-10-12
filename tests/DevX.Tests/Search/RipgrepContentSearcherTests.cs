using DevX.Infrastructure.Storage.SQLite;
using Xunit;

namespace DevX.Tests.Search;

/// <summary>Brief OSS12 item 3: `devx search text` gets ripgrep as its engine when rg is on
/// PATH, with the in-process scan as the fallback. These pin the parts the agent behaviour
/// depends on -- regex, alternation, and honest reporting when the pattern will not compile.</summary>
public sealed class RipgrepContentSearcherTests
{
    /// <summary>ripgrep is an optional engine -- the in-process scan is the documented fallback
    /// -- so these tests are skipped, not failed, where `rg` is not installed. xunit 2.9.2 has no
    /// Assert.Skip, so the guard returns from the test body. The user-visible contract these
    /// stand for is covered engine-independently by <see cref="SearchTextAsyncTests"/>.</summary>
    private static bool RipgrepOnPath()
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("rg", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(10_000);
            return process.ExitCode == 0;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static string WriteTree()
    {
        var root = Directory.CreateTempSubdirectory("devx-rg-search").FullName;
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "src", "Sample.cs"),
            "class Sample\n{\n    void LoadLevel() { }\n    void LoadMoniker() { }\n    int Minimum = 3;\n}\n");
        File.WriteAllText(Path.Combine(root, "notes.md"), "Level loading notes\n");
        return root;
    }

    [Fact]
    public void TrySearch_FindsALiteralInOneCall()
    {
        if (!RipgrepOnPath()) return;   // skip where rg is not installed
        var root = WriteTree();
        Assert.True(RipgrepContentSearcher.TrySearch(root, "LoadLevel", out var hits));
        var hit = Assert.Single(hits);
        Assert.Equal("src/Sample.cs", hit.Path);
        Assert.Equal(3, hit.Line);
        Assert.Contains("LoadLevel", hit.Text);
    }

    [Fact]
    public void TrySearch_MatchesAlternation()
    {
        if (!RipgrepOnPath()) return;   // skip where rg is not installed
        var root = WriteTree();
        Assert.True(RipgrepContentSearcher.TrySearch(root, "LoadLevel|Minimum", out var hits));
        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public void TrySearch_TreatsThePatternAsRegex()
    {
        if (!RipgrepOnPath()) return;   // skip where rg is not installed
        var root = WriteTree();
        Assert.True(RipgrepContentSearcher.TrySearch(root, "Load(Level|Moniker)", out var hits));
        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public void TrySearch_IsCaseInsensitiveLikeTheScanItReplaces()
    {
        if (!RipgrepOnPath()) return;   // skip where rg is not installed
        var root = WriteTree();
        Assert.True(RipgrepContentSearcher.TrySearch(root, "minimum", out var hits));
        Assert.Single(hits);
    }

    [Fact]
    public void TrySearch_FallsBackToAFixedStringWhenThePatternWillNotCompile()
    {
        if (!RipgrepOnPath()) return;   // skip where rg is not installed
        // `GetValueOrDefault(` is a query agents really typed; as a regex it is invalid, and
        // an empty answer would have been indistinguishable from "not in the repository".
        var root = Directory.CreateTempSubdirectory("devx-rg-literal").FullName;
        File.WriteAllText(Path.Combine(root, "a.cs"), "var x = list.GetValueOrDefault(key);\n");
        Assert.True(RipgrepContentSearcher.TrySearch(root, "GetValueOrDefault(", out var hits));
        Assert.Single(hits);
    }

    [Fact]
    public void TrySearch_ReturnsTrueWithNoHitsRatherThanFallingBack()
    {
        if (!RipgrepOnPath()) return;   // skip where rg is not installed
        var root = WriteTree();
        Assert.True(RipgrepContentSearcher.TrySearch(root, "nothing-like-this-anywhere", out var hits));
        Assert.Empty(hits);
    }
}
