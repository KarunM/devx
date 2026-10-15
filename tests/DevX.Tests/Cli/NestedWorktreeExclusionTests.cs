using System.Diagnostics;
using System.Text.Json;
using DevX.Domain;
using File = System.IO.File;
using DevX.Infrastructure.Storage.SQLite;
using Xunit;

namespace DevX.Tests.Cli;

/// <summary>A git worktree or repository nested below the repository root is not that repository's content.</summary>
[Collection("ProcessGlobalState")]
public sealed class NestedWorktreeExclusionTests : CliHarness
{
    private const string Keyword = "KEYWORD_X";

    private static string Repo(out string nested)
    {
        var root = NewRoot("devx-nested-");
        Git(root, "init", "-q");
        File.WriteAllText(Path.Combine(root, "a.py"), $"def alpha():\n    return '{Keyword}'\n");
        Git(root, "add", "a.py");
        Git(root, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "c");
        Git(root, "worktree", "add", "-q", ".claude/worktrees/agent-x", "-b", "ax");
        nested = Path.Combine(root, ".claude", "worktrees", "agent-x");
        return root;
    }

    private static void Cleanup(string root)
    {
        Git(root, "worktree", "remove", "--force", ".claude/worktrees/agent-x");
        Directory.Delete(root, recursive: true);
    }

    private static async Task<string> Session(string dir)
    {
        var (_, output) = await RunAsync(dir, "session", "create", "--format", "json");
        using var json = JsonDocument.Parse(output);
        return json.RootElement.GetProperty("Data").GetProperty("sessionId").GetString()!;
    }

    private static int Count(string text, string needle)
    {
        var n = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + 1, StringComparison.Ordinal)) n++;
        return n;
    }

    [Fact]
    public async Task NestedWorktree_IsNotIndexed_AndTextSearchFindsOneHit_WithAndWithoutRipgrep()
    {
        var root = Repo(out _);
        try
        {
            var id = await Session(root);
            var (code, output) = await RunAsync(root, "index", ".", "--session", id);
            Assert.True(code == 0, output);
            var (_, symbols) = await RunAsync(root, "search", "symbol", "alpha", "--session", id, "--format", "json");
            Assert.DoesNotContain("worktrees", symbols, StringComparison.Ordinal);
            Assert.Contains("alpha", symbols, StringComparison.Ordinal);

            var (_, hits) = await RunAsync(root, "search", "text", Keyword, "--session", id, "--format", "json");
            Assert.Equal(0, Count(hits, ".claude"));
            Assert.Equal(1, Count(hits, "a.py"));

            Directory.CreateDirectory(Path.Combine(root, ".devx"));
            File.WriteAllText(Path.Combine(root, ".devx", "config.json"), "{\"rg\":{\"path\":\"/nonexistent/rg\"}}");
            var (_, scan) = await RunAsync(root, "search", "text", Keyword, "--session", id, "--format", "json");
            Assert.Equal(0, Count(scan, ".claude"));
            Assert.Equal(1, Count(scan, "a.py"));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void Ripgrep_ExcludesNestedWorktree()
    {
        var rg = RipgrepLocator.Locate(Path.GetTempPath()).Executable;
        if (rg is null || OperatingSystem.IsWindows()) return;
        var root = Repo(out _);
        try
        {
            Assert.True(RipgrepContentSearcher.TrySearch(root, Keyword, rg, out var hits, out _, TimeSpan.FromSeconds(20)));
            Assert.Equal(["a.py"], hits.Select(h => h.Path).ToArray());
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task RunningInsideNestedWorktree_IndexesAndFindsItsOwnFiles_NotTheParent()
    {
        var root = Repo(out var nested);
        try
        {
            File.WriteAllText(Path.Combine(nested, "own.py"), "def own_fn():\n    return 'OWN_MARKER'\n");
            var id = await Session(root);
            var (code, output) = await RunAsync(nested, "index", ".", "--session", id);
            Assert.True(code == 0, output);
            var (_, symbols) = await RunAsync(nested, "search", "symbol", "own_fn", "--session", id, "--format", "json");
            Assert.Contains("own_fn", symbols, StringComparison.Ordinal);
            var (_, own) = await RunAsync(nested, "search", "text", "OWN_MARKER", "--session", id, "--format", "json");
            Assert.Equal(1, Count(own, "own.py"));
            var (_, shared) = await RunAsync(nested, "search", "text", Keyword, "--session", id, "--format", "json");
            Assert.Equal(1, Count(shared, "a.py"));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void Find_ExcludesNestedRepoAndSubmoduleLikeDir_ButNotNormalSubdirectory()
    {
        var root = NewRoot("devx-nested-find-");
        try
        {
            Git(root, "init", "-q");
            Directory.CreateDirectory(Path.Combine(root, "src", "pkg"));
            Directory.CreateDirectory(Path.Combine(root, "vendor", "sub"));
            File.WriteAllText(Path.Combine(root, "vendor", "sub", ".git"), "gitdir: ../../.git/modules/sub\n"); // submodule-like pointer file
            Directory.CreateDirectory(Path.Combine(root, "third", "repo"));
            Git(Path.Combine(root, "third", "repo"), "init", "-q");
            Assert.Equal(["third/repo", "vendor/sub"], NestedRepositoryGuard.Find(root).ToArray());
            var files = RepositoryPathGuard.EnumerateFiles(root).Select(p => Path.GetRelativePath(root, p).Replace('\\', '/')).Where(p => !p.StartsWith(".git/")).ToArray();
            Assert.DoesNotContain(files, f => f.StartsWith("vendor/sub", StringComparison.Ordinal));
            Assert.DoesNotContain(files, f => f.StartsWith("third/repo", StringComparison.Ordinal));
            Assert.True(NestedRepositoryGuard.IsInside("third/repo/x.py", ["third/repo"]));
            Assert.False(NestedRepositoryGuard.IsInside("third/repository/x.py", ["third/repo"]));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task NormalSubdirectory_StaysIndexedAndSearchable()
    {
        var root = NewRoot("devx-nested-normal-");
        try
        {
            Git(root, "init", "-q");
            Directory.CreateDirectory(Path.Combine(root, "pkg"));
            File.WriteAllText(Path.Combine(root, "pkg", "m.py"), $"def beta():\n    return '{Keyword}'\n");
            var id = await Session(root);
            var (code, output) = await RunAsync(root, "index", ".", "--session", id);
            Assert.True(code == 0, output);
            var (_, symbols) = await RunAsync(root, "search", "symbol", "beta", "--session", id, "--format", "json");
            Assert.Contains("beta", symbols, StringComparison.Ordinal);
            var (_, hits) = await RunAsync(root, "search", "text", Keyword, "--session", id, "--format", "json");
            Assert.Equal(1, Count(hits, "pkg/m.py"));
            Assert.Empty(NestedRepositoryGuard.Find(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Git(string directory, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
    }
}
