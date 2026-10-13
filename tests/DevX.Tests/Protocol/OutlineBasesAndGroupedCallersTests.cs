using System.Text.Json;
using DevX.Domain;
using DevX.Infrastructure.Storage.SQLite;
using DevX.Indexer;
using DevX.Protocol;
using Microsoft.Data.Sqlite;
using Xunit;
using IOFile = System.IO.File;

namespace DevX.Tests.Protocol;

public sealed class OutlineBasesAndGroupedCallersTests
{
    private sealed record Fixture(string Root, SqliteStorageInitializer Store, ProtocolEngine Engine, SessionState Session);

    private static async Task<Fixture> Create(params (string Path, string Source)[] files)
    {
        var root = Directory.CreateTempSubdirectory("devx-bases-").FullName;
        foreach (var (path, source) in files)
        {
            var full = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await IOFile.WriteAllTextAsync(full, source);
        }
        var store = new SqliteStorageInitializer(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"devx-bases-{Guid.NewGuid():N}.db"), Pooling = false }.ToString());
        await new RepositoryIndexingService(store).IndexAsync(root);
        var repo = await store.GetRepositoryByRootPathAsync(root);
        var session = await store.CreateSessionAsync(new CreateSessionRequest(repo!.Id));
        return new(root, store, new ProtocolEngine(new SearchService(store, store), store, store), session);
    }

    private static Task<ProtocolResponse> Run(Fixture f, string operation, string query, bool full = false)
    {
        var payload = new Dictionary<string, object?> { ["query"] = query };
        if (full) payload["full"] = true;
        return f.Engine.ExecuteAsync(new("1.0", operation, f.Session.Id.Value.ToString(), JsonSerializer.SerializeToElement(payload)));
    }

    private static async Task<SourceReadResponse> ReadType(Fixture f, string query, bool full = false)
    {
        var response = await Run(f, "READ_TYPE", query, full);
        Assert.True(response.Success, string.Join(";", response.Errors.Select(e => e.Message)));
        return response.Data!.Value.Deserialize<SourceReadResponse>()!;
    }

    private static readonly (string, string) PyBase = ("pkg/base.py", "class Base:\n    pass\n\n\nclass Other:\n    pass\n");
    private static readonly (string, string) PyDerived = ("pkg/derived.py", "from pkg.base import Base, Other\nimport pkg.base as basemod\n\n\nclass Derived(Base, basemod.Other, metaclass=type):\n    def run(self):\n        return 1\n");

    [Fact]
    public async Task PythonOutlineResolvesBasesToPathAndLines()
    {
        var f = await Create(PyBase, PyDerived);
        var data = await ReadType(f, "pkg.derived.Derived");
        var lines = data.Source.Split('\n');
        Assert.Equal("class Derived(Base, basemod.Other, metaclass=type):", lines[0]);
        Assert.Equal("bases: Base pkg/base.py:1-2  basemod.Other pkg/base.py:5-6", lines[1]);
        Assert.StartsWith("method run ", lines[2]);
        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public async Task UnresolvedBaseIsMarked()
    {
        var f = await Create(("pkg/m.py", "from typing import Generic, TypeVar\nT = TypeVar('T')\n\n\nclass Box(Generic[T], object, Missing):\n    pass\n"));
        var data = await ReadType(f, "pkg.m.Box");
        Assert.Equal("bases: Generic -  Missing -", data.Source.Split('\n')[1]);
    }

    [Fact]
    public async Task CSharpOutlineResolvesBaseAndInterfaces()
    {
        var f = await Create(
            ("Sample.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"),
            ("Shapes.cs", "namespace Sample;\npublic class Shape\n{\n}\npublic interface IDraw\n{\n    void Draw();\n}\npublic class Circle : Shape, IDraw, System.IDisposable\n{\n    public void Draw() { }\n    public void Dispose() { }\n}\n"));
        var data = await ReadType(f, "Sample.Circle");
        var lines = data.Source.Split('\n');
        Assert.Equal("public class Circle : Shape, IDraw, System.IDisposable", lines[0].Trim());
        Assert.Equal("bases: Shape Shapes.cs:2-4  IDraw Shapes.cs:5-8  System.IDisposable -", lines[1]);
        Assert.Equal("method Draw 11-11 public void Draw() { }", lines[2]);
    }

    [Fact]
    public async Task TypeWithoutBasesHasNoBasesLine()
    {
        var f = await Create(PyBase);
        var data = await ReadType(f, "pkg.base.Base");
        Assert.DoesNotContain("bases:", data.Source);
        Assert.Equal("class Base:", data.Source.Split('\n')[0]);
        var plain = await Create(("pkg/o.py", "class P(object):\n    pass\n"));
        Assert.DoesNotContain("bases:", (await ReadType(plain, "pkg.o.P")).Source);
        Assert.Equal(0, JsonSerializer.SerializeToElement((await ReadType(plain, "pkg.o.P")).Metadata).GetProperty("Bases").GetArrayLength());
    }

    [Fact]
    public async Task MetadataBasesPresent()
    {
        var f = await Create(PyBase, PyDerived);
        var data = await ReadType(f, "pkg.derived.Derived");
        var bases = JsonSerializer.SerializeToElement(data.Metadata).GetProperty("Bases").EnumerateArray().ToArray();
        Assert.Equal(2, bases.Length);
        Assert.Equal("Base", bases[0].GetProperty("Name").GetString());
        Assert.Equal("pkg.base.Base", bases[0].GetProperty("QualifiedName").GetString());
        Assert.Equal("pkg/base.py", bases[0].GetProperty("Path").GetString());
        Assert.Equal(1, bases[0].GetProperty("StartLine").GetInt32());
        Assert.Equal(2, bases[0].GetProperty("EndLine").GetInt32());
        Assert.Equal("basemod.Other", bases[1].GetProperty("Name").GetString());
        Assert.Equal(5, bases[1].GetProperty("StartLine").GetInt32());
        // --full keeps the whole body untouched: no bases line, no Bases metadata.
        var full = await ReadType(f, "pkg.derived.Derived", full: true);
        Assert.DoesNotContain("bases:", full.Source);
        Assert.False(JsonSerializer.SerializeToElement(full.Metadata).TryGetProperty("Bases", out _));
    }

    private const string Target = "def target():\n    return 1\n";

    private static string Callers(int count, int callsEach = 1)
        => "from pkg.t import target\n\n\n" + string.Join("\n\n\n", Enumerable.Range(0, count).Select(i => $"def user{i:00}():\n" + string.Join("\n", Enumerable.Range(0, callsEach).Select(_ => "    target()")) + "\n"));

    private static (string, string)[] ManyFixture(int functions, int callsEach = 1)
        => [("pkg/t.py", Target), ("pkg/u.py", Callers(functions, callsEach))];

    [Fact]
    public async Task ManyCallSitesAreGroupedByEnclosingSymbol()
    {
        // heavy has 3 calls, user00..user11 have 1 each => 15 sites in 13 functions.
        var f = await Create(("pkg/t.py", Target), ("pkg/u.py", Callers(12) + "\n\ndef heavy():\n    target()\n    target()\n    target()\n"));
        var response = await Run(f, ProtocolOperations.GetCallers, "target");
        var text = TextReferences.Format(response.Data!.Value)!;
        var lines = text.Split('\n');
        Assert.StartsWith("pkg.u.<module>.heavy  3 calls  pkg/u.py:", lines[0]);
        Assert.StartsWith("pkg.u.<module>.user00  1 call  pkg/u.py:", lines[1]);
        Assert.Equal("15 call sites in 13 functions (top 10 shown); target pkg/t.py:1; " + TextReferences.CallersNote + "; pass --full for every site", lines[^1]);
        Assert.Equal(10 + 1, lines.Length);
        Assert.DoesNotContain("target()", text);
    }

    [Fact]
    public async Task CallersRemainFlatAtTenSites()
    {
        var f = await Create(ManyFixture(10));
        var response = await Run(f, ProtocolOperations.GetCallers, "target");
        var text = TextReferences.Format(response.Data!.Value)!;
        var lines = text.Split('\n');
        Assert.Equal(11, lines.Length);
        Assert.Equal("pkg/u.py:5 pkg.u.<module>.user00  target()", lines[0]);
        Assert.Equal("10 call sites (10 calls); target pkg/t.py:1; " + TextReferences.CallersNote, lines[^1]);
        Assert.False(response.Data!.Value.GetProperty("Grouped").GetBoolean());
        Assert.False(response.Data!.Value.TryGetProperty("Groups", out _));
    }

    [Fact]
    public async Task FullFlagListsEverySiteOfGroupedCallers()
    {
        var f = await Create(ManyFixture(12));
        var response = await Run(f, ProtocolOperations.GetCallers, "target", full: true);
        var lines = TextReferences.Format(response.Data!.Value)!.Split('\n');
        Assert.Equal(13, lines.Length);
        Assert.Equal("pkg/u.py:5 pkg.u.<module>.user00  target()", lines[0]);
        Assert.Equal("12 call sites (12 calls); target pkg/t.py:1; " + TextReferences.CallersNote, lines[^1]);
        Assert.False(response.Data!.Value.GetProperty("Grouped").GetBoolean());
    }

    [Fact]
    public async Task JsonKeepsReferencesAndAddsGroups()
    {
        var f = await Create(ManyFixture(60));
        var data = (await Run(f, ProtocolOperations.GetCallers, "target")).Data!.Value;
        Assert.True(data.GetProperty("Grouped").GetBoolean());
        Assert.Equal(50, data.GetProperty("References").GetArrayLength());
        Assert.Equal(60, data.GetProperty("Total").GetInt32());
        var groups = data.GetProperty("Groups").EnumerateArray().ToArray();
        Assert.Equal(60, groups.Length);   // grouping covers every site, not only the 50 listed
        Assert.All(groups, g => Assert.Equal(1, g.GetProperty("Count").GetInt32()));
        Assert.Equal("pkg.u.<module>.user00", groups[0].GetProperty("Enclosing").GetString());
        Assert.Equal("pkg/u.py", groups[0].GetProperty("Path").GetString());
        Assert.Equal([5], groups[0].GetProperty("Lines").EnumerateArray().Select(x => x.GetInt32()).ToArray());
        // `references` keeps the old shape: no Grouped, no Groups.
        var refs = (await Run(f, ProtocolOperations.GetReferences, "target")).Data!.Value;
        Assert.False(refs.TryGetProperty("Grouped", out _));
        Assert.False(refs.TryGetProperty("Groups", out _));
        Assert.Contains("pkg/u.py:5 pkg.u.<module>.user00  target()", TextReferences.Format(refs)!);
    }

    [Fact]
    public async Task GroupLinesShowFiveNumbersThenEllipsis()
    {
        var f = await Create(("pkg/t.py", Target), ("pkg/u.py", "from pkg.t import target\n\n\ndef many():\n" + string.Join("\n", Enumerable.Range(0, 8).Select(_ => "    target()")) + "\n\n\ndef b1():\n    target()\n\n\ndef b2():\n    target()\n\n\ndef b3():\n    target()\n"));
        var text = TextReferences.Format((await Run(f, ProtocolOperations.GetCallers, "target")).Data!.Value)!;
        Assert.Equal("pkg.u.<module>.many  8 calls  pkg/u.py:5,6,7,8,9,…", text.Split('\n')[0]);
    }

    [Fact]
    public async Task CSharpCallersUnchanged()
    {
        var root = Directory.CreateTempSubdirectory("devx-bases-cs-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "src", "Sample"));
        await IOFile.WriteAllTextAsync(Path.Combine(root, "src", "Sample", "Sample.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        var callers = string.Join("\n", Enumerable.Range(0, 12).Select(i => $"    public void Caller{i}() {{ Target(); }}"));
        await IOFile.WriteAllTextAsync(Path.Combine(root, "src", "Sample", "Alpha.cs"), $"namespace Sample;\npublic class Alpha\n{{\n    public void Target() {{ }}\n{callers}\n}}\n");
        var store = new SqliteStorageInitializer(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"devx-bases-cs-{Guid.NewGuid():N}.db"), Pooling = false }.ToString());
        await new RepositoryIndexingService(store).IndexAsync(root);
        var repo = await store.GetRepositoryByRootPathAsync(root);
        var session = await store.CreateSessionAsync(new CreateSessionRequest(repo!.Id));
        var engine = new ProtocolEngine(new SearchService(store, store), store, store);
        var response = await engine.ExecuteAsync(new("1.0", ProtocolOperations.GetCallers, session.Id.Value.ToString(), JsonSerializer.SerializeToElement(new { query = "Sample.Alpha.Target" })));
        Assert.True(response.Success);
        Assert.Equal(["Callers"], response.Data!.Value.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(12, response.Data!.Value.Deserialize<CallersResponse>()!.Callers.Callers.Count);
        Assert.Null(TextReferences.Format(response.Data!.Value));
    }

    [Fact]
    public async Task GuidanceMentionsBasesAndGroupedCallersWithinCaps()
    {
        var root = Directory.CreateTempSubdirectory("devx-bases-init-").FullName;
        await DevX.Cli.AgentRulesInitializer.InitializeAsync(root, ["codex"], global: false);
        var content = await IOFile.ReadAllTextAsync(Path.Combine(root, "AGENTS.md"));
        Assert.Contains("`bases:` gives paths: follow a hierarchy one `read type` per level, no search between.", content);
        Assert.Contains("`callers` groups by function; `--full` lists every site.", content);
        var f = await Create(PyBase);
        var tools = (await new McpReferenceServer(f.Engine, f.Store, f.Store, f.Session.Id.Value.ToString()).InitializeAsync()).Tools;
        var callers = tools.Single(t => t.Name == "callers");
        Assert.Contains("Many call sites are grouped by function; full:true lists every site.", callers.Description);
        Assert.Equal(["format", "full", "sessionId", "target"], callers.InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(["target"], callers.InputSchema.GetProperty("required").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.Equal(["format", "sessionId", "target"], tools.Single(t => t.Name == "references").InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Contains("read type bases: carry paths.", tools.Single(t => t.Name == "read").Description);
    }

    [Fact]
    public async Task McpCallersFullTrueListsEverySite()
    {
        var f = await Create(ManyFixture(12));
        var server = new McpReferenceServer(f.Engine, f.Store, f.Store, f.Session.Id.Value.ToString());
        var sid = f.Session.Id.Value.ToString();
        var grouped = await server.CallToolAsync("callers", JsonSerializer.SerializeToElement(new { sessionId = sid, target = "target", format = "agent" }));
        var full = await server.CallToolAsync("callers", JsonSerializer.SerializeToElement(new { sessionId = sid, target = "target", format = "agent", full = true }));
        Assert.Contains("12 call sites in 12 functions (top 10 shown)", TextOf(grouped));
        Assert.Contains("12 call sites (12 calls)", TextOf(full));
    }

    private static string TextOf(McpToolCallResult result)
        => result.Content.ValueKind == JsonValueKind.Object && result.Content.TryGetProperty("text", out var text) ? text.GetString() ?? string.Empty : result.Content.GetRawText();
}
