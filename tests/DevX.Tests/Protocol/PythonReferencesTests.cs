using System.Text.Json;
using DevX.Domain;
using DevX.Infrastructure.Storage.SQLite;
using DevX.Indexer;
using DevX.Protocol;
using Microsoft.Data.Sqlite;
using Xunit;
using IOFile = System.IO.File;

namespace DevX.Tests.Protocol;

public sealed class PythonReferencesTests
{
    private sealed record Fixture(string Root, SqliteStorageInitializer Store, ProtocolEngine Engine, SessionState Session);

    private static async Task<Fixture> Create(params (string Path, string Source)[] files)
    {
        var root = Directory.CreateTempSubdirectory("devx-pyrefs-").FullName;
        foreach (var (path, source) in files)
        {
            var full = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await IOFile.WriteAllTextAsync(full, source);
        }
        var db = Path.Combine(Path.GetTempPath(), $"devx-pyrefs-{Guid.NewGuid():N}.db");
        var store = new SqliteStorageInitializer(new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ToString());
        await new RepositoryIndexingService(store).IndexAsync(root);
        var repo = await store.GetRepositoryByRootPathAsync(root);
        var session = await store.CreateSessionAsync(new CreateSessionRequest(repo!.Id));
        return new(root, store, new ProtocolEngine(new SearchService(store, store), store, store), session);
    }

    private static Task<ProtocolResponse> Run(Fixture f, string operation, string query)
        => f.Engine.ExecuteAsync(new("1.0", operation, f.Session.Id.Value.ToString(), JsonSerializer.SerializeToElement(new { query })));

    private static TextReferencesResponse Text(ProtocolResponse response)
    {
        Assert.True(response.Success, string.Join(";", response.Errors.Select(e => e.Message)));
        return response.Data!.Value.Deserialize<TextReferencesResponse>()!;
    }

    private static readonly (string, string) PkgA = ("pkg/a.py", "def helper():\n    return 1\n\n\nclass K:\n    def m(self):\n        return helper()\n");
    private static readonly (string, string) PkgB = ("pkg/b.py", "from pkg.a import helper\n\nx = helper()\n# helper() in a comment\n");

    [Fact]
    public async Task P1_PythonReferencesReturnUsagesWithEnclosingSymbol()
    {
        var f = await Create(PkgA, PkgB);
        var data = Text(await Run(f, ProtocolOperations.GetReferences, "helper"));
        Assert.Equal("text", data.Mode);
        Assert.Equal(3, data.Total);
        Assert.Equal(
            ["call pkg/a.py:7 pkg.a.K.m", "call pkg/b.py:3 pkg.b.<module>.x", "import pkg/b.py:1 pkg.b.<module>"],
            data.References.Select(r => $"{r.Kind} {r.Path}:{r.Line} {r.Enclosing}").ToArray());
        Assert.DoesNotContain(data.References, r => r.Line == 1 && r.Path == "pkg/a.py");   // definition line
        Assert.DoesNotContain(data.References, r => r.Text.Contains("comment"));            // comment line
        Assert.Equal("return helper()", data.References[0].Text);
        Assert.Equal("pkg/a.py", data.TargetPath);
        Assert.Equal(1, data.TargetLine);
    }

    [Fact]
    public async Task P1_ReferencesAgentFormatIsOneLinePerHitThenFooter()
    {
        var f = await Create(PkgA, PkgB);
        var response = await Run(f, ProtocolOperations.GetReferences, "helper");
        var text = TextReferences.Format(response.Data!.Value)!;
        var lines = text.Split('\n');
        Assert.Equal("pkg/a.py:7 pkg.a.K.m  return helper()", lines[0]);
        Assert.Equal("3 references (calls 2, usages 0, imports 1); target pkg/a.py:1", lines[^1]);
        Assert.Equal(4, lines.Length);
    }

    [Fact]
    public async Task P1_ReferencesDistinguishCallUsageImport()
    {
        var f = await Create(PkgA, ("pkg/c.py", "from pkg.a import helper\nfn = helper\nhelper ()\nprint('helper')\n\"\"\"helper docs\"\"\"\n"));
        var data = Text(await Run(f, ProtocolOperations.GetReferences, "helper"));
        Assert.Equal(
            ["call pkg/a.py:7", "call pkg/c.py:3", "usage pkg/c.py:2", "import pkg/c.py:1"],
            data.References.Select(r => $"{r.Kind} {r.Path}:{r.Line}").Where(x => !x.Contains("c.py:4")).ToArray());
        // 'helper' inside a quoted string on line 4 is a usage by name (documented heuristic only drops quote-leading lines); the docstring line 5 is dropped.
        Assert.DoesNotContain(data.References, r => r.Line == 5);
        Assert.Equal((2, 2, 1), (data.Calls, data.Usages, data.Imports));
    }

    [Fact]
    public async Task P1_ReferencesCapAndRanking()
    {
        var usages = string.Join("\n", Enumerable.Range(0, 60).Select(i => $"v{i} = helper")) + "\nz = helper()\n";
        var f = await Create(PkgA, ("pkg/many.py", usages));
        var data = Text(await Run(f, ProtocolOperations.GetReferences, "helper"));
        Assert.Equal(62, data.Total);
        Assert.Equal(50, data.Returned);
        Assert.Equal(50, data.References.Count);
        Assert.True(data.Truncated);
        Assert.Equal("call", data.References[0].Kind);
        Assert.Equal("call", data.References[1].Kind);
        Assert.Equal("usage", data.References[2].Kind);
        Assert.Equal("pkg/a.py", data.References[0].Path);   // same-file call first
        Assert.EndsWith("showing 50", TextReferences.Format((await Run(f, ProtocolOperations.GetReferences, "helper")).Data!.Value)!.Split('\n')[^1]);
    }

    [Fact]
    public async Task P1_AmbiguousBareNameListsCandidates()
    {
        var f = await Create(("one/x.py", "def dup():\n    return 1\n"), ("two/y.py", "def dup():\n    return 2\n"));
        var response = await Run(f, ProtocolOperations.GetReferences, "dup");
        Assert.False(response.Success);
        var error = Assert.Single(response.Errors);
        Assert.Equal("AmbiguousMatch", error.Code);
        Assert.Equal(2, error.TotalCandidates);
        Assert.Equal(["one.x.<module>.dup", "two.y.<module>.dup"], error.Candidates!.Select(c => c.QualifiedName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task P2_PythonCallersReturnOnlyCallSites()
    {
        var f = await Create(PkgA, PkgB, ("pkg/c.py", "from pkg.a import helper\nfn = helper\n"));
        var data = Text(await Run(f, ProtocolOperations.GetCallers, "helper"));
        Assert.Equal("text", data.Mode);
        Assert.Equal(["pkg/a.py:7", "pkg/b.py:3"], data.References.Select(r => $"{r.Path}:{r.Line}").ToArray());
        Assert.All(data.References, r => Assert.Equal("call", r.Kind));
        Assert.Equal("text-backed: call sites by name, not a resolved call graph", data.Metadata!.Note);
        Assert.Equal(2, data.Total);
    }

    [Fact]
    public async Task P2_PythonCalleesStillUnsupportedWithHint()
    {
        var f = await Create(PkgA, PkgB);
        var response = await Run(f, ProtocolOperations.GetCallees, "helper");
        Assert.False(response.Success);
        var error = Assert.Single(response.Errors);
        Assert.Equal("UnsupportedLanguage", error.Code);
        Assert.Equal("Call graph queries are not supported for Python symbol 'helper'; use `read method helper` and look at the body (callers/references are available, text-backed).", error.Message);
    }

    [Fact]
    public async Task P2_CSharpCallersUnchanged()
    {
        var root = Directory.CreateTempSubdirectory("devx-pyrefs-cs-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "src", "Sample"));
        await IOFile.WriteAllTextAsync(Path.Combine(root, "src", "Sample", "Sample.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        await IOFile.WriteAllTextAsync(Path.Combine(root, "src", "Sample", "Alpha.cs"), "namespace Sample;\npublic class Alpha\n{\n    public void Target() { }\n    public void Caller() { Target(); }\n}\n");
        var store = new SqliteStorageInitializer(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"devx-pyrefs-cs-{Guid.NewGuid():N}.db"), Pooling = false }.ToString());
        await new RepositoryIndexingService(store).IndexAsync(root);
        var repo = await store.GetRepositoryByRootPathAsync(root);
        var session = await store.CreateSessionAsync(new CreateSessionRequest(repo!.Id));
        var engine = new ProtocolEngine(new SearchService(store, store), store, store);
        var response = await engine.ExecuteAsync(new("1.0", ProtocolOperations.GetCallers, session.Id.Value.ToString(), JsonSerializer.SerializeToElement(new { query = "Sample.Alpha.Target" })));
        Assert.True(response.Success);
        var raw = response.Data!.Value.GetRawText();
        Assert.DoesNotContain("\"Mode\"", raw);
        Assert.Equal(["Callers"], response.Data!.Value.EnumerateObject().Select(p => p.Name).ToArray());
        var payload = response.Data!.Value.Deserialize<CallersResponse>()!;
        Assert.Equal("Sample.Alpha.Caller", payload.Callers.Callers.Single().QualifiedName);
        Assert.Equal("Exact", payload.Callers.MatchKind);
        Assert.Equal("Sample.Alpha.Caller", payload.Callers.Callers[0].QualifiedName);
        Assert.Null(TextReferences.Format(response.Data!.Value));
    }

    [Fact]
    public async Task P3_UnindexedRipgrepHitIsNotCountedAsUnsafePath()
    {
        var f = await Create(("a.py", "zzunindexedmarker = 1\n"), ("notes.rst", "zzunindexedmarker in prose\n"));
        var outcome = await f.Store.SearchFileContentsWithCoverageAsync("zzunindexedmarker");
        if (outcome.Coverage.Engine != "ripgrep") return;   // no ripgrep on this PATH: the fallback never sees non-indexed files
        Assert.Equal(["a.py"], outcome.Matches.Select(m => m.Path).ToArray());
        Assert.Equal(1, outcome.Coverage.UnindexedHits);
        Assert.DoesNotContain(outcome.Coverage.Warnings, w => w.Contains("could not be searched safely"));
        Assert.Contains("\"UnindexedHits\":1", JsonSerializer.Serialize(outcome.Coverage));
    }

    [Fact]
    public async Task P3_RipgrepStopsAtTheScanCeilingInsteadOfWaitingOutTheTimeout()
    {
        var f = await Create(("big.py", string.Join("\n", Enumerable.Range(0, 6000).Select(i => $"zzceiling{i % 7} = {i}")) + "\n"));
        var started = DateTime.UtcNow;
        var outcome = await f.Store.SearchFileContentsWithCoverageAsync("zzceiling", 100);
        if (outcome.Coverage.Engine != "ripgrep") return;
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(15), $"took {DateTime.UtcNow - started}");
        Assert.True(outcome.Coverage.ScanCapped);
        Assert.Equal(100, outcome.Matches.Count);
        Assert.Equal("ripgrep", outcome.Coverage.Engine);
    }

    [Fact]
    public async Task P2_PythonCallersWithNoCallSitesIsAnEmptySuccessNotAnError()
    {
        var f = await Create(("solo.py", "def lonely():\n    return 1\n"));
        var response = await Run(f, ProtocolOperations.GetCallers, "lonely");
        var data = Text(response);
        Assert.Empty(data.References);
        Assert.Equal(0, data.Total);
        Assert.Equal("0 call sites (0 calls); target solo.py:1; text-backed: call sites by name, not a resolved call graph", TextReferences.Format(response.Data!.Value));
    }

    [Fact]
    public async Task P2_UnknownNameStillFailsNotFound()
    {
        var f = await Create(PkgA);
        var response = await Run(f, ProtocolOperations.GetReferences, "nosuchthing");
        Assert.False(response.Success);
        Assert.Equal("NotFound", Assert.Single(response.Errors).Code);
        Assert.Equal("Symbol 'nosuchthing' was not found.", response.Errors[0].Message);
    }

    [Fact]
    public async Task P3_AllIndexedHitsReportZeroUnindexed()
    {
        var f = await Create(("a.py", "zzunindexedmarker = 1\n"));
        var outcome = await f.Store.SearchFileContentsWithCoverageAsync("zzunindexedmarker");
        Assert.Equal(0, outcome.Coverage.UnindexedHits);
        Assert.Single(outcome.Matches);
    }

    [Fact]
    public void Classify_DropsCommentsAndQuoteLeadingLinesAndKeepsWholeWordsOnly()
    {
        ContentSearchResult Hit(string text) => new("f", "p.py", 1, 1, text);
        Assert.Null(TextReferences.Classify(Hit("    # helper() here"), "helper"));
        Assert.Null(TextReferences.Classify(Hit("x = 1  # call helper()"), "helper"));
        Assert.Null(TextReferences.Classify(Hit("'''helper'''"), "helper"));
        Assert.Null(TextReferences.Classify(Hit("my_helper(1) + helper_two"), "helper"));
        Assert.Equal("call", TextReferences.Classify(Hit("a = my_helper(1) + helper (2)"), "helper")!.Kind);
        Assert.Equal("import", TextReferences.Classify(Hit("import helper"), "helper")!.Kind);
        Assert.Equal(120, TextReferences.Classify(Hit("x = helper() " + new string('y', 300)), "helper")!.Text.Length);
    }
}
