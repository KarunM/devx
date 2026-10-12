using System.Text.Json;
using DevX.Domain;
using DevX.Indexer;
using DevX.Infrastructure.Storage.SQLite;
using DevX.Protocol;
using Microsoft.Data.Sqlite;
using Xunit;
using IOFile = System.IO.File;

namespace DevX.Tests.Protocol;

/// <summary>1.7.1: C# callers/callees/references resolve like `read`, and same-type overloads answer grouped.</summary>
public sealed class CallersOverloadTests
{
    private const string Locator = """
        namespace Sample;
        public class Locator
        {
            public static string Locate() => Locate("a", "b");
            public static string Locate(string a, string b) { return a + b; }
        }
        """;

    private const string Users = """
        namespace Sample;
        public class UserOne { public void RunOne() { Locator.Locate(); } }
        public class UserTwo { public void RunTwo() { Locator.Locate("x", "y"); } }
        public class Bystander { public void Idle() { var s = "Locate"; } }
        public class Other { public static void Locate(int z) { } }
        """;

    private const string RunTypes = """
        namespace Sample;
        public class Alpha { public void Run() { } }
        public class Beta { public void Run() { } }
        public class Driver { public void Go() { new Alpha().Run(); } }
        """;

    private sealed record Fixture(ProtocolEngine Engine, string SessionId);

    private static async Task<Fixture> Create(params (string File, string Text)[] files)
    {
        var root = Directory.CreateTempSubdirectory("devx-overloads-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "src", "Sample"));
        await IOFile.WriteAllTextAsync(Path.Combine(root, "src", "Sample", "Sample.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        foreach (var (file, text) in files) await IOFile.WriteAllTextAsync(Path.Combine(root, "src", "Sample", file), text);
        var store = new SqliteStorageInitializer(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"devx-overloads-{Guid.NewGuid():N}.db"), Pooling = false }.ToString());
        await new RepositoryIndexingService(store).IndexAsync(root);
        var repo = await store.GetRepositoryByRootPathAsync(root);
        var session = await store.CreateSessionAsync(new CreateSessionRequest(repo!.Id));
        return new Fixture(new ProtocolEngine(new SearchService(store, store), store, store), session.Id.Value.ToString());
    }

    private static Task<ProtocolResponse> Run(Fixture f, string operation, string query)
        => f.Engine.ExecuteAsync(new("1.0", operation, f.SessionId, JsonSerializer.SerializeToElement(new { query })));

    private static string[] Names(JsonElement callers)
        => callers.EnumerateArray().Select(c => c.GetProperty("QualifiedName").GetString()!).ToArray();

    [Fact]
    public async Task Callers_SuffixResolvesLikeRead()
    {
        var f = await Create(("Locator.cs", Locator), ("Users.cs", Users));
        var suffix = await Run(f, ProtocolOperations.GetCallers, "UserOne.RunOne");
        var qualified = await Run(f, ProtocolOperations.GetCallers, "Sample.UserOne.RunOne");
        Assert.True(suffix.Success, string.Join(";", suffix.Errors.Select(e => e.Message)));
        Assert.True(qualified.Success);
        Assert.Equal(Names(qualified.Data!.Value.GetProperty("Callers").GetProperty("Callers")), Names(suffix.Data!.Value.GetProperty("Callers").GetProperty("Callers")));
        Assert.Equal("Sample.UserOne.RunOne", suffix.Data!.Value.GetProperty("Callers").GetProperty("TargetMethod").GetProperty("QualifiedName").GetString());
    }

    [Fact]
    public async Task Callers_OverloadsReturnSeparateCallerGroups()
    {
        var f = await Create(("Locator.cs", Locator), ("Users.cs", Users));
        var response = await Run(f, ProtocolOperations.GetCallers, "Locator.Locate");
        Assert.True(response.Success, string.Join(";", response.Errors.Select(e => e.Message)));
        var overloads = response.Data!.Value.GetProperty("Callers").GetProperty("Overloads").EnumerateArray().ToList();
        Assert.Equal(["Sample.Locator.Locate()", "Sample.Locator.Locate(string, string)"], overloads.Select(o => o.GetProperty("Signature").GetString()!).ToArray());
        Assert.Equal([4, 5], overloads.Select(o => o.GetProperty("StartLine").GetInt32()).ToArray());
        Assert.Equal(["Sample.UserOne.RunOne"], Names(overloads[0].GetProperty("Callers")).Where(n => n != "Sample.Locator.Locate").ToArray());
        Assert.Equal(["Sample.UserTwo.RunTwo"], Names(overloads[1].GetProperty("Callers")).Where(n => n != "Sample.Locator.Locate").ToArray());
        // Neither the unrelated Other.Locate(int) nor the string literal "Locate" is a caller.
        var all = string.Join(",", Names(response.Data!.Value.GetProperty("Callers").GetProperty("Callers")).Order(StringComparer.Ordinal));
        Assert.Equal("Sample.Locator.Locate,Sample.UserOne.RunOne,Sample.UserTwo.RunTwo", all);
    }

    [Fact]
    public async Task Callers_OverloadsMarkDelegation()
    {
        var f = await Create(("Locator.cs", Locator), ("Users.cs", Users));
        var response = await Run(f, ProtocolOperations.GetCallers, "Sample.Locator.Locate");
        var overloads = response.Data!.Value.GetProperty("Callers").GetProperty("Overloads").EnumerateArray().ToList();
        // Locate() delegates to Locate(string, string): only the two-argument group lists it, marked.
        Assert.DoesNotContain(overloads[0].GetProperty("Callers").EnumerateArray(), c => c.TryGetProperty("Relationship", out _));
        var delegation = Assert.Single(overloads[1].GetProperty("Callers").EnumerateArray(), c => c.TryGetProperty("Relationship", out _));
        Assert.Equal("self-delegation", delegation.GetProperty("Relationship").GetString());
        Assert.Equal("Sample.Locator.Locate()", delegation.GetProperty("CallerSignature").GetString());
        var text = TextReferences.Format(response.Data!.Value)!.Split('\n');
        Assert.Equal("Sample.Locator.Locate() src/Sample/Locator.cs:4", text[0]);
        Assert.Equal("  src/Sample/Users.cs:2: Sample.UserOne.RunOne", text[1]);
        Assert.Equal("Sample.Locator.Locate(string, string) src/Sample/Locator.cs:5", text[2]);
        Assert.Equal("  src/Sample/Users.cs:3: Sample.UserTwo.RunTwo", text[3]);
        Assert.Equal("  src/Sample/Locator.cs:4: Sample.Locator.Locate()  [self-delegation]", text[4]);
        Assert.Equal(5, text.Length);
    }

    [Fact]
    public async Task Callers_DifferentTypeAmbiguityListsCandidates()
    {
        var f = await Create(("Run.cs", RunTypes));
        var response = await Run(f, ProtocolOperations.GetCallers, "Run");
        Assert.False(response.Success);
        var error = Assert.Single(response.Errors);
        Assert.Equal("AmbiguousMatch", error.Code);
        Assert.Equal("Symbol 'Run' matched multiple definitions (2); pass a qualified name.", error.Message);
        Assert.Equal(2, error.TotalCandidates);
        Assert.Equal(["Sample.Alpha.Run", "Sample.Beta.Run"], error.Candidates!.Select(c => c.QualifiedName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        // Same error, same candidates as `read method` gives for the same name.
        var read = await Run(f, "READ_METHOD", "Run");
        var readError = Assert.Single(read.Errors);
        Assert.Equal(readError.Code, error.Code);
        Assert.Equal(readError.Message, error.Message);
        Assert.Equal(readError.Candidates!.Select(c => c.QualifiedName), error.Candidates!.Select(c => c.QualifiedName));
    }

    [Fact]
    public async Task Callees_And_References_UseSharedResolver()
    {
        var f = await Create(("Locator.cs", Locator), ("Users.cs", Users), ("Run.cs", RunTypes));
        var callees = await Run(f, ProtocolOperations.GetCallees, "Driver.Go");
        Assert.True(callees.Success, string.Join(";", callees.Errors.Select(e => e.Message)));
        Assert.Contains("Sample.Alpha.Run", Names(callees.Data!.Value.GetProperty("Callees").GetProperty("Callees")));

        var references = await Run(f, ProtocolOperations.GetReferences, "UserOne.RunOne");
        Assert.True(references.Success, string.Join(";", references.Errors.Select(e => e.Message)));

        // Overloads on callees: each answers with its own callees.
        var overloaded = await Run(f, ProtocolOperations.GetCallees, "Locator.Locate");
        Assert.True(overloaded.Success, string.Join(";", overloaded.Errors.Select(e => e.Message)));
        var groups = overloaded.Data!.Value.GetProperty("Callees").GetProperty("Overloads").EnumerateArray().ToList();
        Assert.Equal(2, groups.Count);
        Assert.Equal(["Sample.Locator.Locate"], Names(groups[0].GetProperty("Callees")));
        Assert.Empty(Names(groups[1].GetProperty("Callees")));

        // Different-type ambiguity: read-style candidates, not a bare refusal.
        var ambiguous = await Run(f, ProtocolOperations.GetCallees, "Run");
        var error = Assert.Single(ambiguous.Errors);
        Assert.Equal(2, error.TotalCandidates);
        Assert.Equal(["Sample.Alpha.Run", "Sample.Beta.Run"], error.Candidates!.Select(c => c.QualifiedName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task SingleOverloadCallersKeepShapeAndGainTargetSignature()
    {
        var f = await Create(("Locator.cs", Locator), ("Users.cs", Users));
        var response = await Run(f, ProtocolOperations.GetCallers, "Sample.Other.Locate");
        var callers = response.Data!.Value.GetProperty("Callers");
        Assert.False(callers.TryGetProperty("Overloads", out _));
        Assert.Equal("Sample.Other.Locate(int)", callers.GetProperty("TargetSignature").GetString());
        Assert.Equal("Exact", callers.GetProperty("MatchKind").GetString());
    }

    private const string Shapes = """
        namespace Sample;
        public class Widget
        {
            public Widget() { }
            public Widget(int size) { }
            public static void Log(string message, int level = 0) { }
            public static void Log(string message, string tag) { }
            public static void Join(params string[] parts) { }
            public static void Join(int count, string first) { }
            public static void Unused(int a) { }
            public static void Unused(string a) { }
        }
        public class Maker
        {
            public Widget Small() { return new Widget(); }
            public Widget Big() { return new Widget(3); }
            public void Write() { Widget.Log("a"); }
            public void Tag() { Widget.Log("a", "b"); }
            public void Many() { Widget.Join("a", "b", "c"); }
            public void Counted() { Widget.Join(2, "x"); }
        }
        """;

    [Fact]
    public async Task Callers_ConstructorOverloadsGroupByArity()
    {
        var f = await Create(("Shapes.cs", Shapes));
        var response = await Run(f, ProtocolOperations.GetCallers, "Widget.Widget");
        Assert.True(response.Success, string.Join(";", response.Errors.Select(e => e.Message)));
        var overloads = response.Data!.Value.GetProperty("Callers").GetProperty("Overloads").EnumerateArray().ToList();
        Assert.Equal(["Sample.Widget.Widget()", "Sample.Widget.Widget(int)"], overloads.Select(o => o.GetProperty("Signature").GetString()!).ToArray());
        Assert.Equal(["Sample.Maker.Small"], Names(overloads[0].GetProperty("Callers")));
        Assert.Equal(["Sample.Maker.Big"], Names(overloads[1].GetProperty("Callers")));
    }

    [Fact]
    public async Task Callers_DefaultAndParamsArityRanges()
    {
        var f = await Create(("Shapes.cs", Shapes));
        var log = (await Run(f, ProtocolOperations.GetCallers, "Widget.Log")).Data!.Value.GetProperty("Callers").GetProperty("Overloads").EnumerateArray().ToList();
        // Log("a") fits only the defaulted overload; Log("a", "b") fits both by argument count.
        Assert.Equal(["Sample.Maker.Tag", "Sample.Maker.Write"], Names(log[0].GetProperty("Callers")).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(["Sample.Maker.Tag"], Names(log[1].GetProperty("Callers")));
        var join = (await Run(f, ProtocolOperations.GetCallers, "Widget.Join")).Data!.Value.GetProperty("Callers").GetProperty("Overloads").EnumerateArray().ToList();
        Assert.Equal(["Sample.Widget.Join(string[])", "Sample.Widget.Join(int, string)"], join.Select(o => o.GetProperty("Signature").GetString()!).ToArray());
        // Join("a","b","c") only fits params; Join(2, "x") fits both (params takes any count).
        Assert.Equal(["Sample.Maker.Counted", "Sample.Maker.Many"], Names(join[0].GetProperty("Callers")).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(["Sample.Maker.Counted"], Names(join[1].GetProperty("Callers")));
    }

    [Fact]
    public async Task Callers_OverloadWithoutCallersIsStillListed()
    {
        var f = await Create(("Shapes.cs", Shapes));
        var response = await Run(f, ProtocolOperations.GetCallers, "Widget.Unused");
        Assert.True(response.Success);
        Assert.Equal(
            "Sample.Widget.Unused(int) src/Sample/Shapes.cs:10\n  (none)\nSample.Widget.Unused(string) src/Sample/Shapes.cs:11\n  (none)",
            TextReferences.Format(response.Data!.Value));
    }

    [Fact]
    public async Task Callers_OverloadJsonKeepsExistingFields()
    {
        var f = await Create(("Locator.cs", Locator), ("Users.cs", Users));
        var callers = (await Run(f, ProtocolOperations.GetCallers, "Locator.Locate")).Data!.Value.GetProperty("Callers");
        Assert.Equal("Locator.Locate", callers.GetProperty("Query").GetString());
        Assert.Equal("Exact", callers.GetProperty("MatchKind").GetString());
        Assert.Equal(JsonValueKind.Null, callers.GetProperty("TargetMethod").ValueKind);
        var first = callers.GetProperty("Callers").EnumerateArray().First();
        foreach (var field in new[] { "CallerMethodId", "CallerMethodName", "QualifiedName", "Namespace", "Project", "FilePath", "ContainingType", "CallSiteLine" })
            Assert.True(first.TryGetProperty(field, out _), field);
        Assert.Equal(["Sample.Locator.Locate", "Sample.UserOne.RunOne", "Sample.UserTwo.RunTwo"], Names(callers.GetProperty("Callers")).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Callers_MissingMethodStillNotFound()
    {
        var f = await Create(("Locator.cs", Locator));
        var response = await Run(f, ProtocolOperations.GetCallers, "Nope.Missing");
        Assert.False(response.Success);
        var error = Assert.Single(response.Errors);
        Assert.Equal("NotFound", error.Code);
        Assert.Equal("Method 'Nope.Missing' was not found.", error.Message);
        var references = Assert.Single((await Run(f, ProtocolOperations.GetReferences, "Nope.Missing")).Errors);
        Assert.Equal("Symbol 'Nope.Missing' was not found.", references.Message);
    }

    [Fact]
    public async Task PythonCallersUnchanged()
    {
        var root = Directory.CreateTempSubdirectory("devx-overloads-py-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "pkg"));
        await IOFile.WriteAllTextAsync(Path.Combine(root, "pkg", "a.py"), "def helper():\n    return 1\n\n\ndef caller():\n    return helper()\n");
        var store = new SqliteStorageInitializer(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"devx-overloads-py-{Guid.NewGuid():N}.db"), Pooling = false }.ToString());
        await new RepositoryIndexingService(store).IndexAsync(root);
        var repo = await store.GetRepositoryByRootPathAsync(root);
        var session = await store.CreateSessionAsync(new CreateSessionRequest(repo!.Id));
        var engine = new ProtocolEngine(new SearchService(store, store), store, store);
        var response = await engine.ExecuteAsync(new("1.0", ProtocolOperations.GetCallers, session.Id.Value.ToString(), JsonSerializer.SerializeToElement(new { query = "helper" })));
        Assert.True(response.Success);
        Assert.Equal("pkg/a.py:6 pkg.a.<module>.caller  return helper()\n1 call sites (1 calls); target pkg/a.py:1; " + TextReferences.CallersNote, TextReferences.Format(response.Data!.Value));
        Assert.DoesNotContain("Overloads", response.Data!.Value.GetRawText());
    }
}
