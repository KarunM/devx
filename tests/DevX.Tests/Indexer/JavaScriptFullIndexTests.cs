using DevX.Indexer;
using DevX.Infrastructure.Storage.SQLite;
using Microsoft.Data.Sqlite;
using Xunit;
using IOFile = System.IO.File;

namespace DevX.Tests.Indexer;

public sealed class JavaScriptFullIndexTests
{
    [Fact]
    public async Task FullIndex_DiscoversJavaScript_SkipsMinifiedAndNodeModules()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"devx-js-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        var store = new SqliteStorageInitializer(connectionString);
        await store.InitializeAsync();

        var root = Directory.CreateTempSubdirectory("devx-js-repo").FullName;
        Directory.CreateDirectory(Path.Combine(root, "js"));
        Directory.CreateDirectory(Path.Combine(root, "node_modules", "lib"));
        IOFile.WriteAllText(Path.Combine(root, "js", "app.js"), "function appEntryPoint() {\n  return 1;\n}\n");
        IOFile.WriteAllText(Path.Combine(root, "js", "vendor.min.js"), "function minifiedVendorFn(){return 2}\n");
        IOFile.WriteAllText(Path.Combine(root, "node_modules", "lib", "dep.js"), "function nodeModulesFn() {\n}\n");

        await new RepositoryIndexingService(store).IndexAsync(root);

        Assert.Single(await store.FindSymbolsByNameAsync("appEntryPoint"));
        Assert.Empty(await store.FindSymbolsByNameAsync("minifiedVendorFn"));
        Assert.Empty(await store.FindSymbolsByNameAsync("nodeModulesFn"));
    }
}
