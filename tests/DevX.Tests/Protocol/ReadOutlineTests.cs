using System.Text.Json;
using DevX.Domain;
using DevX.Infrastructure.Storage.SQLite;
using DevX.Indexer;
using DevX.Protocol;
using Microsoft.Data.Sqlite;
using Xunit;
using IOFile = System.IO.File;

namespace DevX.Tests.Protocol;

public sealed class ReadOutlineTests
{
    private const string CSharpSource = "namespace Demo;\n\npublic class Greeter\n{\n    public string Name { get; set; } = \"x\";\n\n    public string Hello(string who)\n    {\n        var secretBodyMarker = who;\n        return \"Hello \" + secretBodyMarker;\n    }\n\n    public void Wave()\n    {\n        System.Console.WriteLine(\"wave\");\n    }\n}\n";

    private sealed record Fixture(string Root, string Db, SqliteStorageInitializer Store, ProtocolEngine Engine, SessionState Session);

    private static async Task<Fixture> Create(string source, string relativePath)
    {
        var root = Directory.CreateTempSubdirectory("devx-outline-").FullName;
        await IOFile.WriteAllTextAsync(Path.Combine(root, relativePath), source);
        var db = Path.Combine(Path.GetTempPath(), $"devx-outline-{Guid.NewGuid():N}.db");
        var store = new SqliteStorageInitializer(new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ToString());
        await new RepositoryIndexingService(store).IndexAsync(root);
        var repo = await store.GetRepositoryByRootPathAsync(root);
        var session = await store.CreateSessionAsync(new CreateSessionRequest(repo!.Id));
        return new(root, db, store, new ProtocolEngine(new SearchService(store, store), store, store), session);
    }

    private static Task<ProtocolResponse> Run(Fixture f, string operation, string query, bool full = false)
    {
        var payload = new Dictionary<string, object?> { ["query"] = query };
        if (full) payload["full"] = true;
        return f.Engine.ExecuteAsync(new("1.0", operation, f.Session.Id.Value.ToString(), JsonSerializer.SerializeToElement(payload)));
    }

    private static async Task<SourceReadResponse> ReadType(Fixture f, bool full)
    {
        var response = await Run(f, "READ_TYPE", "Greeter", full);
        Assert.True(response.Success, string.Join(";", response.Errors.Select(e => e.Message)));
        return response.Data!.Value.Deserialize<SourceReadResponse>()!;
    }

    [Fact]
    public async Task O1_TypeOutlineListsDirectMembersAndSignature()
    {
        var f = await Create(CSharpSource, "Greeter.cs");
        var data = await ReadType(f, full: false);
        var lines = data.Source.Split('\n');
        Assert.Equal("public class Greeter", lines[0].Trim());
        Assert.Contains(lines, l => l == "method Hello 7-11 public string Hello(string who)");
        Assert.Contains(lines, l => l == "method Wave 13-16 public void Wave()");
        Assert.DoesNotContain("secretBodyMarker", data.Source);
        Assert.DoesNotContain("WriteLine", data.Source);
        Assert.False(data.Truncated);
        Assert.Equal(lines.Length, data.ReturnedLines);
        var metadata = JsonSerializer.Serialize(data.Metadata);
        Assert.Contains("\"Outline\":true", metadata);
        Assert.Contains("\"Hint\":\"pass --full for the body\"", metadata);
        Assert.Contains("\"BodyLines\":15", metadata);
        var count = JsonDocument.Parse(metadata).RootElement.GetProperty("MemberCount").GetInt32();
        Assert.True(count >= 2, $"member count {count}; outline was:\n{data.Source}");
        Assert.Equal(1 + count, lines.Length);
    }

    [Fact]
    public async Task O1_PythonMultiLineSignatureIsKeptWholeAndNestedMembersExcluded()
    {
        var f = await Create("class Alpha(\n    Base,\n    Other):\n    def run(self):\n        def inner():\n            return 1\n        return inner\n", "alpha.py");
        var response = await Run(f, "READ_TYPE", "Alpha");
        var data = response.Data!.Value.Deserialize<SourceReadResponse>()!;
        var lines = data.Source.Split('\n');
        Assert.Equal("class Alpha(", lines[0]);
        Assert.Equal("    Other):", lines[2]);
        Assert.Equal("bases: Base -  Other -", lines[3]);   // OSS26 B-1: the added line; unresolved bases are marked
        Assert.Equal(5, lines.Length);
        Assert.Contains("run", lines[4]);
        Assert.DoesNotContain("inner", data.Source);
    }

    [Fact]
    public async Task O1_TypeOutlineBytesAreSmallerThanBody()
    {
        var f = await Create(CSharpSource, "Greeter.cs");
        var outline = await ReadType(f, full: false);
        var body = await ReadType(f, full: true);
        Assert.True(outline.Source.Length < body.Source.Length, $"{outline.Source.Length} !< {body.Source.Length}");
    }

    [Fact]
    public async Task O1_FullReturnsWholeBodyUnchanged()
    {
        var f = await Create(CSharpSource, "Greeter.cs");
        var body = await ReadType(f, full: true);
        var expected = string.Join('\n', CSharpSource.Split('\n')[2..17]);
        Assert.Equal(expected, body.Source);
        Assert.Equal(15, body.ReturnedLines);
        Assert.DoesNotContain("Outline", JsonSerializer.Serialize(body.Metadata));
    }

    [Fact]
    public async Task O2_ReadToolDescriptionNamesQualifiedNameAndOutline()
    {
        var f = await Create(CSharpSource, "Greeter.cs");
        var server = new McpReferenceServer(f.Engine, f.Store, f.Store, f.Session.Id.Value.ToString());
        var tools = (await server.InitializeAsync()).Tools;
        var read = tools.Single(t => t.Name == "read");
        Assert.Contains("target: qualified name (e.g. pkg.mod.Name) or repo path for kind=file.", read.Description);
        Assert.Contains("read type (outline) answers 'which methods/fields does X have'; --full only for bodies.", read.Description);
        var args = read.InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Contains("target", args); Assert.Contains("kind", args); Assert.Contains("full", args); Assert.Contains("range", args);
        Assert.Contains(tools, t => t.Name == "search"); Assert.Contains(tools, t => t.Name == "patch.apply");
        // agent rendering over MCP: header + outline, no body
        var result = await server.CallToolAsync("read", JsonSerializer.SerializeToElement(new { sessionId = f.Session.Id.Value.ToString(), kind = "type", target = "Greeter" }));
        var text = result.Content.GetProperty("text").GetString()!;
        Assert.Contains("public class Greeter", text);
        Assert.Contains("Hello", text);
        Assert.DoesNotContain("secretBodyMarker", text);
    }

    private static async Task WithReadOnlyDb(Fixture f, Func<Task> act)
    {
        var mode = IOFile.GetUnixFileMode(f.Db);
        IOFile.SetUnixFileMode(f.Db, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        try { await act(); }
        finally { IOFile.SetUnixFileMode(f.Db, mode); }
    }

    [Fact]
    public async Task O3_SearchSucceedsOnReadOnlySessionDbWithDiagnostic()
    {
        var f = await Create(CSharpSource, "Greeter.cs");
        await WithReadOnlyDb(f, async () =>
        {
            var response = await Run(f, ProtocolOperations.SearchSymbol, "Greeter");
            Assert.True(response.Success, string.Join(";", response.Errors.Select(e => e.Message)));
            var diagnostic = Assert.Single(response.Diagnostics, d => d.Message.StartsWith("history not recorded:"));
            Assert.Equal("HistoryNotRecorded", diagnostic.Code);
            Assert.Contains("Greeter", response.Data!.Value.GetRawText());
        });
    }

    [Fact]
    public async Task O3_ReadMethodSucceedsOnReadOnlySessionDb()
    {
        var f = await Create(CSharpSource, "Greeter.cs");
        await WithReadOnlyDb(f, async () =>
        {
            var response = await Run(f, "READ_METHOD", "Greeter.Hello");
            Assert.True(response.Success, string.Join(";", response.Errors.Select(e => e.Message)));
            Assert.Single(response.Diagnostics, d => d.Message.StartsWith("history not recorded:"));
            Assert.Contains("secretBodyMarker", response.Data!.Value.GetRawText());
        });
    }

    [Fact]
    public async Task O3_WritableDbRecordsHistoryWithoutDiagnostic()
    {
        var f = await Create(CSharpSource, "Greeter.cs");
        var response = await Run(f, ProtocolOperations.SearchSymbol, "Greeter");
        Assert.True(response.Success);
        Assert.Empty(response.Diagnostics);
    }
}
