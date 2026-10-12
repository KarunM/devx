using System.Text.Json;
using DevX.Domain;
using DevX.Infrastructure.Storage.SQLite;
using DevX.Indexer;
using DevX.Protocol;
using Microsoft.Data.Sqlite;
using Xunit;
using IOFile = System.IO.File;

namespace DevX.Tests.Protocol;

/// <summary>
/// The contract the LG1 arm-B transcript broke: what `search` returns, `read` accepts, and
/// every MCP tool answers an agent in one shape.
/// </summary>
public sealed class McpToolContractTests
{
    [Fact]
    public async Task EveryNameSearchReturns_IsAcceptedByReadVerbatim()
    {
        var (server, session, _) = await CreateServerAsync("devx-mcp-roundtrip");
        var id = session.Id.Value.ToString();

        var search = await server.CallToolAsync("search", JsonSerializer.SerializeToElement(new { sessionId = id, kind = "symbol", query = "Run" }));
        var names = NamesFrom(TextOf(search));

        Assert.NotEmpty(names);
        foreach (var name in names)
        {
            var read = await server.CallToolAsync("read", JsonSerializer.SerializeToElement(new { sessionId = id, target = name }));
            Assert.False(read.IsError, $"read rejected the name search returned: {name} -- {TextOf(read)}");
        }
    }

    [Fact]
    public async Task DottedPythonName_ReadsAsASymbolAndNotAsAFile()
    {
        var (server, session, _) = await CreateServerAsync("devx-mcp-pyname", plainFolder: true);
        var id = session.Id.Value.ToString();

        var search = await server.CallToolAsync("search", JsonSerializer.SerializeToElement(new { sessionId = id, kind = "symbol", query = "mcpPlainFunction" }));
        var name = NamesFrom(TextOf(search)).FirstOrDefault();

        Assert.False(string.IsNullOrEmpty(name));
        // Path.HasExtension("plain.mcpPlainFunction") is true, which is what sent this to a
        // file read and produced "file missing: plain.mcpPlainFunction".
        var read = await server.CallToolAsync("read", JsonSerializer.SerializeToElement(new { sessionId = id, target = name }));
        Assert.False(read.IsError, TextOf(read));
        Assert.Contains("mcpPlainFunction", TextOf(read));
    }

    [Fact]
    public async Task ReadContext_OptInReturnsMetadataAndAgentTextShowsOverflow()
    {
        var repositoryRoot = Directory.CreateTempSubdirectory("devx-mcp-read-context").FullName;
        Directory.CreateDirectory(Path.Combine(repositoryRoot, "tests"));
        await IOFile.WriteAllTextAsync(Path.Combine(repositoryRoot, "expr.py"), "class Expr:\n    def round(self):\n        return self._mag()\n    def _mag(self):\n        return 0\n");
        foreach (var index in Enumerable.Range(1, 7))
        {
            var testDirectory = Path.Combine(repositoryRoot, "tests", $"group{index}");
            Directory.CreateDirectory(testDirectory);
            await IOFile.WriteAllTextAsync(Path.Combine(testDirectory, "test_expr.py"), "def test_round():\n    return Expr().round()\n");
        }
        var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"devx-mcp-read-context-{Guid.NewGuid():N}.db") }.ToString();
        var store = new SqliteStorageInitializer(connectionString);
        var indexer = new RepositoryIndexingService(store);
        await indexer.IndexAsync(repositoryRoot);
        var repositoryId = new RepositoryId(Guid.Parse(await GetRepositoryIdAsync(connectionString)));
        var session = await store.CreateSessionAsync(new CreateSessionRequest(repositoryId));
        var server = new McpReferenceServer(new ProtocolEngine(new SearchService(store, store), store, store), store, store, session.Id.Value.ToString());
        var id = session.Id.Value.ToString();

        var tools = await server.ListToolsAsync();
        var readSchema = tools.Single(tool => tool.Name == "read").InputSchema;
        Assert.True(readSchema.GetProperty("properties").TryGetProperty("context", out _));
        var plain = await server.CallToolAsync("read", JsonSerializer.SerializeToElement(new { sessionId = id, kind = "method", target = "expr.Expr.round", format = "json" }));
        Assert.False(ContainsProperty(plain.Content, "context"));

        var enriched = await server.CallToolAsync("read", JsonSerializer.SerializeToElement(new { sessionId = id, kind = "method", target = "expr.Expr.round", context = true, format = "json" }));
        var context = FindProperty(enriched.Content, "context");
        Assert.Equal(5, FindProperty(context, "testNameMatches").GetArrayLength());
        Assert.True(FindProperty(context, "testNameMatchesTruncated").GetBoolean());

        var agent = await server.CallToolAsync("read", JsonSerializer.SerializeToElement(new { sessionId = id, kind = "method", target = "expr.Expr.round", context = true }));
        Assert.Contains("navigation candidates (incomplete; name/path evidence only)", TextOf(agent));
        Assert.Contains("+more test candidates", TextOf(agent));
    }

    [Fact]
    public async Task ToolListing_KeepsNamesAndBoundsTheGuidanceText()
    {
        var (server, _, _) = await CreateServerAsync("devx-mcp-listing");
        var tools = await server.ListToolsAsync();
        Assert.Equal(
            ["build", "callees", "callers", "edit.replace", "index", "patch.apply", "patch.preview", "patch.validate", "read", "references", "report", "search", "snapshot", "test", "verify"],
            tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.All(tools, t => Assert.True(t.Description.Length <= 320, $"{t.Name}: {t.Description.Length}"));
        var total = tools.Sum(t => System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(new { name = t.Name, description = t.Description, inputSchema = t.InputSchema })));
        Assert.InRange(total, 1, 9000);
        string D(string n) => tools.Single(t => t.Name == n).Description;
        Assert.Contains("for callers/usages use callers/references instead of text search", D("search"));
        Assert.Contains("declaration line (bases for a class)", D("search"));
        Assert.Contains("Bare or Class.member names resolve when unique", D("read"));
        Assert.Contains("who calls this", D("callers"));
        Assert.Contains("what does this call", D("callees"));
        Assert.Contains("where is this used", D("references"));
        Assert.Contains("outline", D("snapshot"));
    }

    [Fact]
    public async Task MissingDottedName_ComesBackWithANameThatCanBePasted()
    {
        var (server, session, _) = await CreateServerAsync("devx-mcp-suggest");
        var id = session.Id.Value.ToString();

        // A dotted miss used to answer "Symbol '...' is not in this workspace" with an empty
        // suggestion list, which is what sent the LG1 agent to grep; the last segment is
        // always searchable, so the miss now carries names that read accepts.
        var read = await server.CallToolAsync("read", JsonSerializer.SerializeToElement(new { sessionId = id, target = "Sample.Program.Runn" }));

        Assert.True(read.IsError, TextOf(read));
        Assert.Contains("Did you mean:", TextOf(read));
    }

    [Fact]
    public async Task LineRangeRead_AcceptsBothSpellings_EvenWhenTheKindSaysSection()
    {
        var (server, session, root) = await CreateServerAsync("devx-mcp-range");
        var id = session.Id.Value.ToString();
        var file = Path.Combine(root, "src", "Sample", "Program.cs");
        var before = await IOFile.ReadAllTextAsync(file);

        // LG1 asked for lines 595-615 with kind=section and got "Read section requests must
        // use 'file#heading'". A range names a file read whatever the label says, and both
        // spellings are the same request.
        foreach (var spelling in new[] { "1-3", "1:3" })
        {
            var read = await server.CallToolAsync("read", JsonSerializer.SerializeToElement(new { sessionId = id, target = "src/Sample/Program.cs", kind = "section", range = spelling }));
            Assert.False(read.IsError, $"{spelling}: {TextOf(read)}");
            Assert.Contains("namespace Sample;", TextOf(read));
        }

        Assert.Equal(before, await IOFile.ReadAllTextAsync(file));
    }


    [Fact]
    public async Task EveryToolAnswersTheAgentInOneShape_OnSuccessAndOnError()
    {
        var (server, session, _) = await CreateServerAsync("devx-mcp-shape");
        var id = session.Id.Value.ToString();

        // Tools disagreed with each other: `search` answered a one-line success while the next
        // tool returned a raw JSON envelope on failure -- and raw JSON is exactly what an
        // agent asked to avoid when it did not pass format:json.
        var success = await server.CallToolAsync("search", JsonSerializer.SerializeToElement(new { sessionId = id, kind = "symbol", query = "Run" }));
        // A failing call from a tool that used to leak `{"error": "..."}` as its answer.
        var failure = await server.CallToolAsync("read", JsonSerializer.SerializeToElement(new { sessionId = id, target = "NoSuch.Symbol" }));
        var index = await server.CallToolAsync("index", JsonSerializer.SerializeToElement(new { sessionId = id }));

        foreach (var result in new[] { success, failure, index })
        {
            Assert.Equal(JsonValueKind.Object, result.Content.ValueKind);
            Assert.True(result.Content.TryGetProperty("text", out var text), result.Content.GetRawText());
            Assert.Equal(JsonValueKind.String, text.ValueKind);
        }

        Assert.True(failure.IsError);
        Assert.StartsWith("error: ", failure.Content.GetProperty("text").GetString());

        // format:json is the one way to still get the envelope, and it is unchanged.
        var json = await server.CallToolAsync("search", JsonSerializer.SerializeToElement(new { sessionId = id, kind = "symbol", query = "Run", format = "json" }));
        Assert.Contains("symbols", json.Content.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }


    private static string TextOf(McpToolCallResult result)
        => result.Content.ValueKind == JsonValueKind.Object && result.Content.TryGetProperty("text", out var text)
            ? text.GetString() ?? string.Empty
            : result.Content.GetRawText();

    private static bool ContainsProperty(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
            return element.EnumerateObject().Any(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || ContainsProperty(property.Value, name));
        if (element.ValueKind == JsonValueKind.Array)
            return element.EnumerateArray().Any(item => ContainsProperty(item, name));
        return false;
    }

    private static JsonElement FindProperty(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
                if (ContainsProperty(property.Value, name)) return FindProperty(property.Value, name);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                if (ContainsProperty(item, name)) return FindProperty(item, name);
        }
        throw new KeyNotFoundException($"JSON property '{name}' was not found.");
    }

    /// <summary>The agent-format lines are "kind qualifiedName path:start-end".</summary>
    private static List<string> NamesFrom(string agentText)
    {
        var names = new List<string>();
        foreach (var line in agentText.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 && parts[1].Contains('.') && !parts[1].StartsWith("--")) names.Add(parts[1]);
        }
        return names;
    }

    private static async Task<(McpReferenceServer Server, SessionState Session, string RepositoryRoot)> CreateServerAsync(string prefix, bool plainFolder = false)
    {
        var repositoryRoot = Directory.CreateTempSubdirectory(prefix).FullName;
        if (plainFolder)
        {
            await IOFile.WriteAllTextAsync(Path.Combine(repositoryRoot, "plain.py"), "def mcpPlainFunction():\n    return 1\n");
        }
        else
        {
            var projectDirectory = Path.Combine(repositoryRoot, "src", "Sample");
            Directory.CreateDirectory(projectDirectory);
            await IOFile.WriteAllTextAsync(Path.Combine(projectDirectory, "Sample.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);
            await IOFile.WriteAllTextAsync(Path.Combine(projectDirectory, "Program.cs"), """
                namespace Sample;
                public static class Program
                {
                    public static void Run() { }
                }
                """);
        }

        var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"devx-mcp-{Guid.NewGuid():N}.db") }.ToString();
        var store = new SqliteStorageInitializer(connectionString);
        await store.InitializeAsync();
        var indexer = new RepositoryIndexingService(store);
        await indexer.IndexAsync(repositoryRoot);

        var repositoryId = await GetRepositoryIdAsync(connectionString);
        var session = await store.CreateSessionAsync(new CreateSessionRequest(new RepositoryId(Guid.Parse(repositoryId))));
        var engine = new ProtocolEngine(new SearchService(store, store), store, store);
        var server = new McpReferenceServer(
            engine,
            store,
            store,
            session.Id.Value.ToString(),
            (files, cancellationToken) => indexer.ReindexFilesAsync(repositoryRoot, files, cancellationToken),
            "1.6.5",
            cancellationToken => indexer.IndexAsync(repositoryRoot, cancellationToken),
            cancellationToken => store.InitializeAsync(cancellationToken),
            null);
        return (server, session, repositoryRoot);
    }

    private static async Task<string> GetRepositoryIdAsync(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id FROM Repositories LIMIT 1";
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
