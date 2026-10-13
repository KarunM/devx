using DevX.Application;
using DevX.Domain;
using DevX.Cli;
using DevX.Infrastructure.Storage.SQLite;
using DevX.Indexer;
using DevX.Protocol;
using Microsoft.Data.Sqlite;

static partial class Program
{
    static async Task<int> McpServeAsync(string connectionString)
    {
        var store = new SqliteStorageInitializer(connectionString);
        await store.InitializeAsync();
        var root = FindRepositoryRoot(Environment.CurrentDirectory);
        var repository = new Repository(new RepositoryId(StableId(root)), Path.GetFileName(root), root);
        await store.UpsertRepositoryAsync(repository);
        var session = await AutomaticSessionResolver.GetOrCreateAsync(store, root, repository);
        var engine = new ProtocolEngine(new SearchService(store, store), store, store);
        var indexer = new RepositoryIndexingService(store);
        var reference = new McpReferenceServer(
            engine,
            store,
            store,
            session.Id.Value.ToString(),
            (files, cancellationToken) => indexer.ReindexFilesAsync(root, files, cancellationToken),
            GetProductVersion(),
            cancellationToken => indexer.IndexAsync(root, cancellationToken),
            cancellationToken => store.InitializeAsync(cancellationToken),
            // Left null so the server's start-of-session refresh and its "already current" answer
            // degrade to indexing rather than failing when git cannot report changed files.
            changedFilesAsync: indexer.GetChangedFilesAsync);
        await new McpStdioServer(reference).RunAsync(Console.In, Console.Out);
        return 0;
    }
}
