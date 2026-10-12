using DevX.Domain;
using DevX.Indexer;
using DevX.Infrastructure.Storage.SQLite;
using DevX.Protocol;
using Xunit;
using IOFile = System.IO.File;

namespace DevX.Tests.Protocol;

public sealed class RepositoryPathGuardTests
{
    [Fact]
    public void TryResolve_RejectsTraversalAndSiblingPrefix()
    {
        var parent = Directory.CreateTempSubdirectory("devx-path-guard-");
        var root = Path.Combine(parent.FullName, "repo");
        var sibling = Path.Combine(parent.FullName, "repo-sibling");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(sibling);
        try
        {
            Assert.False(RepositoryPathGuard.TryResolve(root, "../repo-sibling/secret.txt", out _, out var traversalError));
            Assert.Contains("outside", traversalError, StringComparison.OrdinalIgnoreCase);
            Assert.False(RepositoryPathGuard.TryResolve(root, Path.Combine(sibling, "secret.txt"), out _, out var siblingError));
            Assert.Contains("outside", siblingError, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(parent.FullName, recursive: true); }
    }

    [Fact]
    public void TryResolve_AllowsTrustedRootAliasButRejectsLinksBelowIt()
    {
        var parent = Directory.CreateTempSubdirectory("devx-path-alias-");
        var root = Path.Combine(parent.FullName, "repo");
        var outside = Path.Combine(parent.FullName, "outside");
        var alias = Path.Combine(parent.FullName, "repo-alias");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        IOFile.WriteAllText(Path.Combine(root, "local.txt"), "local");
        IOFile.WriteAllText(Path.Combine(outside, "secret.txt"), "outside");
        try
        {
            try { Directory.CreateSymbolicLink(alias, root); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            Assert.True(RepositoryPathGuard.TryResolve(alias, "local.txt", out var resolvedAliasPath, out var aliasError), aliasError);
            Assert.Equal(Path.Combine(alias, "local.txt"), resolvedAliasPath);

            Directory.CreateSymbolicLink(Path.Combine(root, "linked.txt"), Path.Combine(outside, "secret.txt"));
            Assert.False(RepositoryPathGuard.TryResolve(root, "linked.txt", out _, out var linkError));
            Assert.Contains("symbolic link", linkError, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(parent.FullName, recursive: true); }
    }

    [Fact]
    public void TryResolve_RejectsDanglingFileAndDirectoryLinks()
    {
        var parent = Directory.CreateTempSubdirectory("devx-path-dangling-");
        var root = Path.Combine(parent.FullName, "repo");
        Directory.CreateDirectory(root);
        try
        {
            IOFile.CreateSymbolicLink(Path.Combine(root, "dangling.txt"), Path.Combine(parent.FullName, "missing-target.txt"));
            Directory.CreateSymbolicLink(Path.Combine(root, "dangling-dir"), Path.Combine(parent.FullName, "missing-target-dir"));

            Assert.False(RepositoryPathGuard.TryResolve(root, "dangling.txt", out _, out var fileError));
            Assert.Contains("symbolic link", fileError, StringComparison.OrdinalIgnoreCase);
            Assert.False(RepositoryPathGuard.TryResolve(root, "dangling-dir/secret.txt", out _, out var directoryError));
            Assert.Contains("symbolic link", directoryError, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(parent.FullName, recursive: true); }
    }

    [Fact]
    public void EnumerateFiles_DoesNotEnterLinkedDirectoriesOrReturnLinkedFiles()
    {
        var parent = Directory.CreateTempSubdirectory("devx-path-enumerate-");
        var root = Path.Combine(parent.FullName, "repo");
        var outside = Path.Combine(parent.FullName, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        IOFile.WriteAllText(Path.Combine(root, "local.py"), "local = 1");
        IOFile.WriteAllText(Path.Combine(outside, "secret.py"), "secret = 1");
        try
        {
            try
            {
                IOFile.CreateSymbolicLink(Path.Combine(root, "linked.py"), Path.Combine(outside, "secret.py"));
                Directory.CreateSymbolicLink(Path.Combine(root, "linked-dir"), outside);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            var discovered = RepositoryPathGuard.EnumerateFiles(root, "*.py").ToArray();
            Assert.Equal([Path.Combine(root, "local.py")], discovered);
        }
        finally { Directory.Delete(parent.FullName, recursive: true); }
    }

    [Fact]
    public async Task FullIndexAndGlobalTextSearchIgnoreLinkedFiles()
    {
        var parent = Directory.CreateTempSubdirectory("devx-path-index-");
        var root = Path.Combine(parent.FullName, "repo");
        var outside = Path.Combine(parent.FullName, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        IOFile.WriteAllText(Path.Combine(root, "local.py"), "local_marker = 1");
        IOFile.WriteAllText(Path.Combine(outside, "secret.py"), "secret_marker = 1");
        try
        {
            try { IOFile.CreateSymbolicLink(Path.Combine(root, "linked.py"), Path.Combine(outside, "secret.py")); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(Path.GetTempPath(), $"devx-path-index-{Guid.NewGuid():N}.db")
            }.ToString();
            var store = new SqliteStorageInitializer(connectionString);
            await new RepositoryIndexingService(store).IndexAsync(root);

            Assert.NotNull(await store.GetFileSnapshotAsync("local.py"));
            Assert.Null(await store.GetFileSnapshotAsync("linked.py"));
            Assert.Empty(await store.SearchFileContentsAsync("secret_marker"));
        }
        finally { Directory.Delete(parent.FullName, recursive: true); }
    }

    [Fact]
    public async Task ProtocolReadsRejectAStaleIndexedPathThatBecameALink()
    {
        var parent = Directory.CreateTempSubdirectory("devx-path-read-");
        var root = parent.FullName;
        var sourcePath = Path.Combine(root, "Sample.cs");
        var outside = Path.Combine(Path.GetDirectoryName(root)!, $"{Path.GetFileName(root)}-outside.cs");
        await IOFile.WriteAllTextAsync(Path.Combine(root, "Sample.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        await IOFile.WriteAllTextAsync(sourcePath, "namespace Sample; public class Demo { public void Run() { } }");
        await IOFile.WriteAllTextAsync(outside, "namespace Secret; public class Hidden { public void ReadMe() { } }");
        var dbPath = Path.Combine(Path.GetTempPath(), $"devx-path-read-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteStorageInitializer(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
            await new RepositoryIndexingService(store).IndexAsync(root);
            var repository = (await store.GetRepositoryByRootPathAsync(root))!;
            var session = await store.CreateSessionAsync(new CreateSessionRequest(repository.Id));
            var engine = new ProtocolEngine(new SearchService(store, store), store, store);
            var traversalRead = await engine.ExecuteAsync(new ProtocolRequest(
                "1.0", "READ_FILE", session.Id.Value.ToString(),
                System.Text.Json.JsonSerializer.SerializeToElement(new { query = "../" + Path.GetFileName(outside), range = "1:2" })));
            Assert.False(traversalRead.Success);
            Assert.DoesNotContain("Secret", traversalRead.Data?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);

            try { IOFile.Delete(sourcePath); IOFile.CreateSymbolicLink(sourcePath, outside); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            var readFile = await engine.ExecuteAsync(new ProtocolRequest(
                "1.0", "READ_FILE", session.Id.Value.ToString(),
                System.Text.Json.JsonSerializer.SerializeToElement(new { query = "Sample.cs", range = "1:2" })));
            Assert.False(readFile.Success);
            Assert.Contains(readFile.Errors, error => error.Code == "ValidationError");

            var readMethod = await engine.ExecuteAsync(new ProtocolRequest(
                "1.0", "READ_METHOD", session.Id.Value.ToString(),
                System.Text.Json.JsonSerializer.SerializeToElement(new { query = "Sample.Demo.Run" })));
            Assert.False(readMethod.Success);
            Assert.Contains(readMethod.Errors, error => error.Code == "ValidationError");

            var readSection = await engine.ExecuteAsync(new ProtocolRequest(
                "1.0", "READ_SECTION", session.Id.Value.ToString(),
                System.Text.Json.JsonSerializer.SerializeToElement(new { file = "Sample.cs", heading = "Sample.Demo.Run" })));
            Assert.False(readSection.Success);
            Assert.Contains(readSection.Errors, error => error.Code == "ValidationError");
        }
        finally
        {
            if (IOFile.Exists(dbPath)) IOFile.Delete(dbPath);
            if (IOFile.Exists(dbPath + "-shm")) IOFile.Delete(dbPath + "-shm");
            if (IOFile.Exists(dbPath + "-wal")) IOFile.Delete(dbPath + "-wal");
            if (IOFile.Exists(outside)) IOFile.Delete(outside);
            parent.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ProtocolReadDoesNotFallBackToGlobalSymbolsForAnUnknownRepositoryId()
    {
        var parent = Directory.CreateTempSubdirectory("devx-path-unknown-repository-");
        var root = parent.FullName;
        await IOFile.WriteAllTextAsync(Path.Combine(root, "sample.py"), "def secret_symbol():\n    return 'private'\n");
        var dbPath = Path.Combine(Path.GetTempPath(), $"devx-path-unknown-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteStorageInitializer(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
            await new RepositoryIndexingService(store).IndexAsync(root);
            var unknownRepositoryId = new RepositoryId(Guid.NewGuid());
            var session = await store.CreateSessionAsync(new CreateSessionRequest(unknownRepositoryId));
            var engine = new ProtocolEngine(new SearchService(store, store), store, store);

            var response = await engine.ExecuteAsync(new ProtocolRequest(
                "1.0", "READ_METHOD", session.Id.Value.ToString(),
                System.Text.Json.JsonSerializer.SerializeToElement(new { query = "sample.secret_symbol" })));

            Assert.False(response.Success);
            Assert.Contains(response.Errors, error => error.Code == "RepositoryNotIndexed");
            Assert.DoesNotContain("private", response.Data?.ToString() ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            if (IOFile.Exists(dbPath)) IOFile.Delete(dbPath);
            if (IOFile.Exists(dbPath + "-shm")) IOFile.Delete(dbPath + "-shm");
            if (IOFile.Exists(dbPath + "-wal")) IOFile.Delete(dbPath + "-wal");
            parent.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task MarkdownSectionReadDoesNotReturnAnotherRepositoryWithSameRelativePath()
    {
        var parent = Directory.CreateTempSubdirectory("devx-path-section-scope-");
        var rootA = Path.Combine(parent.FullName, "repo-a");
        var rootB = Path.Combine(parent.FullName, "repo-b");
        Directory.CreateDirectory(rootA);
        Directory.CreateDirectory(rootB);
        await IOFile.WriteAllTextAsync(Path.Combine(rootA, "CHECKLIST.md"), "# Shared common text\n\n## LocalHeading\nOnly local text.\n");
        await IOFile.WriteAllTextAsync(Path.Combine(rootB, "CHECKLIST.md"), "# Shared common text\n\n## ForeignHeading\nForeign secret text.\n");
        var dbPath = Path.Combine(Path.GetTempPath(), $"devx-path-section-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteStorageInitializer(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
            var indexer = new RepositoryIndexingService(store);
            await indexer.IndexAsync(rootB);
            await indexer.IndexAsync(rootA);
            var repositoryA = (await store.GetRepositoryByRootPathAsync(rootA))!;
            var repositoryB = (await store.GetRepositoryByRootPathAsync(rootB))!;
            var scopedStore = Assert.IsAssignableFrom<IRootScopedFileSnapshotStore>(store);
            var snapshotA = await scopedStore.GetFileSnapshotAsync(repositoryA.Id, rootA, "CHECKLIST.md");
            var snapshotB = await scopedStore.GetFileSnapshotAsync(repositoryB.Id, rootB, "CHECKLIST.md");
            Assert.Contains(snapshotA!.Methods, method => method.Name == "LocalHeading");
            Assert.Contains(snapshotB!.Methods, method => method.Name == "ForeignHeading");
            var legacySnapshot = await store.GetFileSnapshotAsync("CHECKLIST.md");
            Assert.Contains(legacySnapshot!.Methods, method => method.Name == "ForeignHeading");
            var sessionA = await store.CreateSessionAsync(new CreateSessionRequest(repositoryA.Id));
            var sessionB = await store.CreateSessionAsync(new CreateSessionRequest(repositoryB.Id));
            var engine = new ProtocolEngine(new SearchService(store, store), store, store);

            var foreignOwnerResponse = await engine.ExecuteAsync(new ProtocolRequest(
                "1.0", "READ_SECTION", sessionB.Id.Value.ToString(),
                System.Text.Json.JsonSerializer.SerializeToElement(new { file = "CHECKLIST.md", heading = "ForeignHeading" })));
            Assert.True(foreignOwnerResponse.Success, string.Join("; ", foreignOwnerResponse.Errors.Select(error => $"{error.Code}: {error.Message}")));
            Assert.Contains("Foreign secret text", foreignOwnerResponse.Data!.Value.GetProperty("Source").GetString());

            var response = await engine.ExecuteAsync(new ProtocolRequest(
                "1.0", "READ_SECTION", sessionA.Id.Value.ToString(),
                System.Text.Json.JsonSerializer.SerializeToElement(new { file = "CHECKLIST.md", heading = "ForeignHeading" })));

            Assert.False(response.Success);
            Assert.DoesNotContain(response.Diagnostics, diagnostic => diagnostic.Message.Contains("Foreign secret text", StringComparison.Ordinal));
            Assert.DoesNotContain(response.Errors, error => error.Message.Contains("Foreign secret text", StringComparison.Ordinal));

            var localResponse = await engine.ExecuteAsync(new ProtocolRequest(
                "1.0", "READ_SECTION", sessionA.Id.Value.ToString(),
                System.Text.Json.JsonSerializer.SerializeToElement(new { file = "CHECKLIST.md", heading = "LocalHeading" })));
            Assert.True(localResponse.Success);
            Assert.Contains("Only local text", localResponse.Data!.Value.GetProperty("Source").GetString());
        }
        finally
        {
            if (IOFile.Exists(dbPath)) IOFile.Delete(dbPath);
            if (IOFile.Exists(dbPath + "-shm")) IOFile.Delete(dbPath + "-shm");
            if (IOFile.Exists(dbPath + "-wal")) IOFile.Delete(dbPath + "-wal");
            parent.Delete(recursive: true);
        }
    }

    [Fact]
    public void TextReplace_RejectsLinkedTargetWithoutChangingOutsideFile()
    {
        var parent = Directory.CreateTempSubdirectory("devx-path-replace-");
        var root = Path.Combine(parent.FullName, "repo");
        var outside = Path.Combine(parent.FullName, "secret.txt");
        Directory.CreateDirectory(root);
        IOFile.WriteAllText(outside, "outside");
        try
        {
            try { IOFile.CreateSymbolicLink(Path.Combine(root, "linked.txt"), outside); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            var result = new TextReplaceService().Apply(root, "linked.txt", "outside", "changed");
            Assert.False(result.Success);
            Assert.Contains("symbolic link", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("outside", IOFile.ReadAllText(outside));

            var traversalResult = new TextReplaceService().Apply(root, "../secret.txt", "outside", "changed");
            Assert.False(traversalResult.Success);
            Assert.Equal("outside", IOFile.ReadAllText(outside));
        }
        finally { Directory.Delete(parent.FullName, recursive: true); }
    }
}
