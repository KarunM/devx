using System.Text.Json;
using DevX.Domain;
using DevX.Indexer;
using DevX.Infrastructure.Storage.SQLite;
using DevX.Protocol;
using Microsoft.Data.Sqlite;
using Xunit;
using IOFile = System.IO.File;

namespace DevX.Tests.Protocol;

public sealed class ReadSectionHashHeadingTests
{
    [Fact]
    public async Task HeadingContainingHashIsReadAfterSplittingAtTheFirstHash()
    {
        var root = Directory.CreateTempSubdirectory("devx-hash-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        await IOFile.WriteAllTextAsync(Path.Combine(root, "docs/SETUP.md"), "# Setup\n\n## Post-merge #30 follow-ups\n\nthe body\n\n## Other\n\nx\n");
        await IOFile.WriteAllTextAsync(Path.Combine(root, "infra.tf"), "resource \"a\" \"b\" {\n  x = 1\n}\n");
        var db = Path.Combine(Path.GetTempPath(), $"devx-hash-{Guid.NewGuid():N}.db");
        var store = new SqliteStorageInitializer(new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ToString());
        await new RepositoryIndexingService(store).IndexAsync(root);
        var repo = await store.GetRepositoryByRootPathAsync(root);
        var session = await store.CreateSessionAsync(new CreateSessionRequest(repo!.Id));
        var engine = new ProtocolEngine(new SearchService(store, store), store, store);

        const string target = "docs/SETUP.md#Post-merge #30 follow-ups";
        var at = SectionTarget.SeparatorIndex(target);
        var response = await engine.ExecuteAsync(new("1.0", "READ_SECTION", session.Id.Value.ToString(),
            JsonSerializer.SerializeToElement(new { file = target[..at], heading = target[(at + 1)..] })));

        Assert.True(response.Success, string.Join(";", response.Errors.Select(e => e.Message)));
        var text = response.Data!.Value.ToString();
        Assert.Contains("the body", text);
        Assert.DoesNotContain("## Other", text);

        var snapshot = await engine.ExecuteAsync(new("1.0", ProtocolOperations.GetFileSnapshot, session.Id.Value.ToString(),
            JsonSerializer.SerializeToElement(new { query = "infra.tf" })));
        Assert.True(snapshot.Success, string.Join(";", snapshot.Errors.Select(e => e.Message)));
        Assert.Contains("a.b", snapshot.Data!.Value.ToString());
    }
}
