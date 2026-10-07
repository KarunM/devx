using DevX.Indexer;
using DevX.Infrastructure.Storage.SQLite;
using Microsoft.Data.Sqlite;
using Xunit;
using IOFile = System.IO.File;

namespace DevX.Tests.Indexer;

public sealed class PlainFolderIndexingTests
{
    [Fact]
    public async Task IndexAsync_IndexesGlobalNamespaceCSharpTypesInPlainFolder()
    {
        var root = Directory.CreateTempSubdirectory("devx-plain-global").FullName;
        await IOFile.WriteAllTextAsync(Path.Combine(root, "Widget.cs"), "public class Widget { public int Frobnicate() => 1; }");
        var store = CreateStore();

        try
        {
            await new RepositoryIndexingService(store).IndexAsync(root);
            Assert.Contains(await store.FindSymbolsByQualifiedNameAsync("Widget.Frobnicate"), symbol => symbol.Name == "Frobnicate");
            Assert.Empty(await store.FindSymbolsByQualifiedNameAsync(".Widget.Frobnicate"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task IndexAsync_IndexesPlainFolderAndFindsPythonFunction()
    {
        var root = Directory.CreateTempSubdirectory("devx-plain-index").FullName;
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        await IOFile.WriteAllTextAsync(Path.Combine(root, "a.py"), "def plainFolderFunction():\n    return 1\n");
        await IOFile.WriteAllTextAsync(Path.Combine(root, "README.md"), "# Plain folder\n");
        await IOFile.WriteAllTextAsync(Path.Combine(root, "sub", "b.py"), "def nestedPlainFunction():\n    return 2\n");
        var store = CreateStore();

        var result = await new RepositoryIndexingService(store).IndexAsync(root);

        Assert.True(result.FilesIndexed >= 3);
        Assert.Null(result.Reason);
        Assert.Contains(await store.FindSymbolsByNameAsync("plainFolderFunction"), symbol => symbol.Name == "plainFolderFunction");
    }

    [Fact]
    public async Task IndexAsync_RespectsGitignoreAndAlwaysSkipsNodeModules()
    {
        var root = Directory.CreateTempSubdirectory("devx-plain-ignore").FullName;
        Directory.CreateDirectory(Path.Combine(root, "ignored"));
        Directory.CreateDirectory(Path.Combine(root, "node_modules"));
        await IOFile.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "ignored/\n");
        await IOFile.WriteAllTextAsync(Path.Combine(root, "ignored", "ignoredFunction.py"), "def ignoredFunction(): pass\n");
        await IOFile.WriteAllTextAsync(Path.Combine(root, "node_modules", "x.py"), "def nodeModulesFunction(): pass\n");
        await IOFile.WriteAllTextAsync(Path.Combine(root, "kept.py"), "def keptFunction(): pass\n");
        var store = CreateStore();

        var result = await new RepositoryIndexingService(store).IndexAsync(root);

        Assert.Equal(1, result.FilesIndexed);
        Assert.Empty(await store.FindSymbolsByNameAsync("ignoredFunction"));
        Assert.Empty(await store.FindSymbolsByNameAsync("nodeModulesFunction"));
        Assert.Single(await store.FindSymbolsByNameAsync("keptFunction"));
    }

    [Fact]
    public async Task IndexAsync_ProjectFolderKeepsProjectBasedCSharpIndexing()
    {
        var root = Directory.CreateTempSubdirectory("devx-project-index").FullName;
        await IOFile.WriteAllTextAsync(Path.Combine(root, "App.csproj"), "<Project />");
        await IOFile.WriteAllTextAsync(Path.Combine(root, "Program.cs"), "namespace Sample; public class Program { public void Run() { } }");
        var store = CreateStore();

        var result = await new RepositoryIndexingService(store).IndexAsync(root);

        Assert.Equal(1, result.FilesIndexed);
        Assert.Single(await store.FindSymbolsByNameAsync("Program"));
    }

    [Fact]
    public async Task IndexAsync_EmptyFolderReturnsReason()
    {
        var root = Directory.CreateTempSubdirectory("devx-empty-index").FullName;
        var result = await new RepositoryIndexingService(CreateStore()).IndexAsync(root);

        Assert.Equal(0, result.FilesIndexed);
        Assert.Equal("no files with an indexable extension found", result.Reason);
    }

    private static SqliteStorageInitializer CreateStore()
        => new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"devx-plain-{Guid.NewGuid():N}.db") }.ToString());
}
