using System.Text.Json;
using DevX.Domain;
using DevX.Infrastructure.Storage.SQLite;
using DevX.Indexer;
using DevX.Protocol;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevX.Tests.Protocol;

public sealed class ReadFamilyFixesTests
{
    private sealed record Fixture(string Root, SqliteStorageInitializer Store, ProtocolEngine Engine, SessionState Session);

    private static async Task<Fixture> Create(string source, string relativePath = "sample.py")
    {
        var root = Directory.CreateTempSubdirectory("devx-read-fixes-").FullName;
        var full = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await System.IO.File.WriteAllTextAsync(full, source);
        var store = new SqliteStorageInitializer(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"devx-read-fixes-{Guid.NewGuid():N}.db") }.ToString());
        await new RepositoryIndexingService(store).IndexAsync(root);
        var repo = await store.GetRepositoryByRootPathAsync(root);
        var session = await store.CreateSessionAsync(new CreateSessionRequest(repo!.Id));
        return new(root, store, new ProtocolEngine(new SearchService(store, store), store, store), session);
    }

    private static Task<ProtocolResponse> Read(Fixture f, string operation, string query, object? extra = null)
    {
        var payload = new Dictionary<string, object?> { ["query"] = query };
        if (extra is not null)
            foreach (var property in JsonDocument.Parse(JsonSerializer.Serialize(extra)).RootElement.EnumerateObject()) payload[property.Name] = property.Value;
        return f.Engine.ExecuteAsync(new("1.0", operation, f.Session.Id.Value.ToString(), JsonSerializer.SerializeToElement(payload)));
    }

    [Fact]
    public async Task RA_TypeReadFullReturnsClassBody()
    {
        var f = await Create("class Alpha:\n    def run(self):\n        return 1\n");
        var response = await Read(f, "READ_TYPE", "Alpha", new { full = true });
        Assert.True(response.Success);
        var data = response.Data!.Value.Deserialize<SourceReadResponse>()!;
        Assert.Contains("class Alpha:", data.Source);
        Assert.Equal(3, data.ReturnedLines);
    }

    [Fact]
    public async Task TypeReadReturnsOutlineByDefault()
    {
        var f = await Create("class Alpha(Base):\n    def run(self):\n        return 1\n");
        var response = await Read(f, "READ_TYPE", "Alpha");
        Assert.True(response.Success);
        var data = response.Data!.Value.Deserialize<SourceReadResponse>()!;
        Assert.Contains("class Alpha(Base):", data.Source);
        Assert.Contains("run", data.Source);
        Assert.DoesNotContain("return 1", data.Source);
        Assert.Equal(3, data.ReturnedLines);   // signature + OSS26 `bases: Base -` + the one member
        Assert.Contains("\"Outline\":true", JsonSerializer.Serialize(data.Metadata));
    }

    [Fact]
    public async Task RB_UniqueSegmentSuffixResolvesAndRecordsSuffix()
    {
        // "mod.Alpha.run" is a segment-aligned tail of pkg.mod.Alpha.run that no module-path form covers.
        var f = await Create("class Alpha:\n    def run(self):\n        return 1\n", Path.Combine("pkg", "mod.py"));
        var response = await Read(f, "READ_METHOD", "mod.Alpha.run");
        Assert.True(response.Success);
        Assert.Equal("suffix", response.Data!.Value.GetProperty("Metadata").GetProperty("ResolvedVia").GetString());
        Assert.Contains("return 1", response.Data.Value.GetProperty("Source").GetString());
    }

    [Fact]
    public async Task RB_ContainerQualifiedShortFormRecordsModulePath()
    {
        // "Alpha.run" is resolved by the module-path matcher (container + member), which runs before suffix matching; ResolvedVia names that mechanism.
        var f = await Create("class Alpha:\n    def run(self):\n        return 1\n");
        var response = await Read(f, "READ_METHOD", "Alpha.run");
        Assert.True(response.Success);
        Assert.Equal("module-path", response.Data!.Value.GetProperty("Metadata").GetProperty("ResolvedVia").GetString());
    }

    [Fact]
    public async Task RC_AmbiguousSuffixIsCappedAndReportsTotal()
    {
        var f = await Create(string.Join("\n", Enumerable.Range(0, 12).Select(i => $"class C{i}:\n    def run(self):\n        return {i}")));
        var response = await Read(f, "READ_METHOD", "run");
        Assert.False(response.Success);
        var error = Assert.Single(response.Errors);
        Assert.Equal("AmbiguousMatch", error.Code);
        Assert.Equal(10, error.Candidates!.Count);
        Assert.Contains("12", error.Message);
        Assert.Contains("qualified", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RD_ElementReadIsNotTruncatedWhenItsFullSpanWasReturned()
    {
        var f = await Create("class Alpha:\n    def run(self):\n        return 1\n");
        var response = await Read(f, "READ_METHOD", "sample.Alpha.run");
        Assert.True(response.Success);
        Assert.False(response.Data!.Value.GetProperty("Truncated").GetBoolean());
    }
}
