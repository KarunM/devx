using System.Text.Json;
using DevX.Domain;
using DevX.Infrastructure.Storage.SQLite;
using DevX.Indexer;
using DevX.Protocol;
using Microsoft.Data.Sqlite;
using Xunit;
using IOFile = System.IO.File;

namespace DevX.Tests.Protocol;

public sealed class McpReferenceServerIntegrationTests
{
    [Fact]
    public async Task InitializeAsync_ListsReferenceTools()
    {
        var (server, _, _) = await CreateServerAsync("devx-mcp-init");

        var response = await server.InitializeAsync();

        Assert.Equal("2025-06-18", response.ProtocolVersion);
        Assert.Equal("1.6.5", response.ServerInfo.Version);
        Assert.Contains(response.Tools, tool => tool.Name == "search" && tool.CapabilityId == "search");
        Assert.Contains(response.Tools, tool => tool.Name == "read" && tool.CapabilityId == "read");
        Assert.Contains(response.Tools, tool => tool.Name == "patch.validate" && tool.CapabilityId == "patch.validate");
        Assert.Contains(response.Tools, tool => tool.Name == "patch.apply" && tool.CapabilityId == "patch.apply");
        Assert.Contains(response.Tools, tool => tool.Name == "edit.replace");
        Assert.Contains(response.Tools, tool => tool.Name == "verify" && tool.CapabilityId == "verify");
        Assert.Contains(response.Tools, tool => tool.Name == "patch.preview");
        Assert.Contains(response.Tools, tool => tool.Name == "index" && tool.CapabilityId == "index");
        Assert.Contains(response.Tools, tool => tool.Name == "report" && tool.CapabilityId == "report");
    }

    [Fact]
    public async Task StdioServer_InitializeIndexAndReplace_UpdatesFileAndReportsAmbiguity()
    {
        var (server, session, root) = await CreateServerAsync("devx-mcp-replace", indexRepository: false, plainFolder: true);
        var file = Path.Combine(root, "replace.md");
        await IOFile.WriteAllTextAsync(file, "first old\nsecond old\n");
        var id = session.Id.Value.ToString();
        var requests = string.Join("\n", [
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\"}}",
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{}}",
            $"{{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{{\"name\":\"index\",\"arguments\":{{\"sessionId\":\"{id}\"}}}}}}",
            $"{{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{{\"name\":\"edit.replace\",\"arguments\":{{\"sessionId\":\"{id}\",\"file\":\"replace.md\",\"old\":\"first\",\"new\":\"updated\"}}}}}}",
            $"{{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{{\"name\":\"edit.replace\",\"arguments\":{{\"sessionId\":\"{id}\",\"file\":\"replace.md\",\"old\":\"old\",\"new\":\"new\"}}}}}}"
        ]) + "\n";
        var output = new StringWriter(); await new McpStdioServer(server).RunAsync(new StringReader(requests), output);
        var responses = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line)).ToArray();
        Assert.Contains("edit.replace", responses[1].RootElement.GetRawText()); Assert.Contains("patch.preview", responses[1].RootElement.GetRawText());
        Assert.False(responses[3].RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal("updated old\nsecond old\n", await IOFile.ReadAllTextAsync(file));
        var failed = responses[4].RootElement.GetProperty("result"); Assert.True(failed.GetProperty("isError").GetBoolean()); Assert.Contains("2 times", failed.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task EditReplace_VerifiesOnlyWhenAskedAndHonoursTheMode()
    {
        var (server, session, root) = await CreateServerAsync("devx-mcp-verify", plainFolder: true,
            verifyAfterEditFactory: _ => new VerifyAfterEdit(
                _ => Task.FromResult(new ExecutionRecord("build", true, "", [], "PASS build")),
                _ => Task.FromResult(new ExecutionRecord("test", true, "", [], "PASS 1 tests")),
                _ => []));
        await IOFile.WriteAllTextAsync(Path.Combine(root, "edit.md"), "old");
        // 1.6.14 behaviour change: an edit with no `verify` argument is NOT verified, so an edit
        // costs one call. Verifying is opt-in (brief OSS14 adjustment).
        var plain = await server.CallToolAsync("edit.replace", JsonSerializer.SerializeToElement(new
        {
            sessionId = session.Id.Value.ToString(),
            file = "edit.md",
            old = "old",
            @new = "new"
        }));
        Assert.False(plain.IsError);
        Assert.DoesNotContain("PASS build", plain.Content.GetProperty("text").GetString());

        await IOFile.WriteAllTextAsync(Path.Combine(root, "edit.md"), "old");
        var verified = await server.CallToolAsync("edit.replace", JsonSerializer.SerializeToElement(new
        {
            sessionId = session.Id.Value.ToString(),
            file = "edit.md",
            old = "old",
            @new = "new",
            verify = true
        }));
        Assert.False(verified.IsError);
        var verifiedText = verified.Content.GetProperty("text").GetString();
        Assert.Contains("PASS build", verifiedText);
        Assert.Contains("PASS 1 tests", verifiedText);

        // `verify: "build"` is the build alone, so a caller that only wants to know it compiles
        // does not pay for the suite.
        await IOFile.WriteAllTextAsync(Path.Combine(root, "edit.md"), "old");
        var buildOnly = await server.CallToolAsync("edit.replace", JsonSerializer.SerializeToElement(new
        {
            sessionId = session.Id.Value.ToString(),
            file = "edit.md",
            old = "old",
            @new = "new",
            verify = "build"
        }));
        Assert.False(buildOnly.IsError);
        var buildOnlyText = buildOnly.Content.GetProperty("text").GetString();
        Assert.Contains("PASS build", buildOnlyText);
        Assert.DoesNotContain("PASS 1 tests", buildOnlyText);
    }

    [Fact]
    public async Task CallToolAsync_SearchAndRead_ReturnSuccessfulPayloads()
    {
        var (server, session, _) = await CreateServerAsync("devx-mcp-read");

        var search = await server.CallToolAsync("search", JsonSerializer.SerializeToElement(new
        {
            sessionId = session.Id.Value.ToString(),
            kind = "symbol",
            query = "Run"
        }));

        Assert.False(search.IsError);
        Assert.True(search.Content.GetProperty("success").GetBoolean());

        var read = await server.CallToolAsync("read", JsonSerializer.SerializeToElement(new
        {
            sessionId = session.Id.Value.ToString(),
            kind = "file",
            target = "src/Sample/Program.cs"
        }));

        Assert.False(read.IsError);
        Assert.True(read.Content.GetProperty("success").GetBoolean());
        Assert.Contains("Program", read.Content.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CallToolAsync_DefaultAgentFormat_AndJsonRoundTrip()
    {
        var (server, session, _) = await CreateServerAsync("devx-mcp-agent-format");
        var args = new { sessionId = session.Id.Value.ToString(), kind = "symbol", query = "Run" };

        var agent = await server.CallToolAsync("search", JsonSerializer.SerializeToElement(args));
        var json = await server.CallToolAsync("search", JsonSerializer.SerializeToElement(new { args.sessionId, args.kind, args.query, format = "json" }));

        Assert.False(agent.IsError);
        Assert.Contains("method Sample.Program.Run", agent.Content.GetProperty("text").GetString());
        Assert.True(agent.Content.GetProperty("success").GetBoolean());
        Assert.True(json.Content.GetProperty("success").GetBoolean());
        Assert.True(json.Content.TryGetProperty("data", out _));
    }

    [Fact]
    public async Task CallToolAsync_BuildAndTest_UseCompactAgentTextAndKeepJsonPayload()
    {
        var (server, session, _) = await CreateServerAsync("devx-mcp-compact-output");
        var buildArguments = JsonSerializer.SerializeToElement(new { sessionId = session.Id.Value.ToString() });
        var build = await server.CallToolAsync("build", buildArguments);
        var buildJson = await server.CallToolAsync("build", JsonSerializer.SerializeToElement(new { sessionId = session.Id.Value.ToString(), format = "json" }));
        var test = await server.CallToolAsync("test", JsonSerializer.SerializeToElement(new { sessionId = session.Id.Value.ToString() }));

        Assert.False(build.IsError);
        Assert.Contains("PASS build in", build.Content.GetProperty("text").GetString());
        Assert.Contains("warnings) log:", build.Content.GetProperty("text").GetString());
        Assert.True(buildJson.Content.TryGetProperty("data", out _));
        Assert.Contains("PASS tests", test.Content.GetProperty("text").GetString());
        Assert.Contains("log:", test.Content.GetProperty("text").GetString());
    }

    [Fact]
    public async Task CallToolAsync_TextSearchAndSymbolFallback_ReturnAgentMatchLines()
    {
        var (server, session, repositoryRoot) = await CreateServerAsync("devx-mcp-text-agent");
        await IOFile.WriteAllTextAsync(Path.Combine(repositoryRoot, "notes.md"), "UniquePreview phrase for text search.");
        var indexed = await server.CallToolAsync("index", JsonSerializer.SerializeToElement(new
        {
            sessionId = session.Id.Value.ToString(),
            files = new[] { "notes.md" }
        }));
        Assert.False(indexed.IsError);

        var textSearch = await server.CallToolAsync("search", JsonSerializer.SerializeToElement(new
        {
            sessionId = session.Id.Value.ToString(),
            kind = "text",
            query = "UniquePreview"
        }));
        var fallback = await server.CallToolAsync("search", JsonSerializer.SerializeToElement(new
        {
            sessionId = session.Id.Value.ToString(),
            query = "UniquePreview"
        }));

        AssertAgentSearchText(textSearch);
        AssertAgentSearchText(fallback);

        void AssertAgentSearchText(McpToolCallResult result)
        {
            var text = result.Content.GetProperty("text").GetString()!;
            Assert.StartsWith("notes.md:1: UniquePreview phrase for text search.", text);
            // Every engine now states what it searched; complete coverage adds no "unknown" notice.
            var lines = text.Split('\n');
            Assert.Equal(2, lines.Length);
            Assert.StartsWith("Searched: ", lines[1]);
            Assert.DoesNotContain("unknown", lines[1]);
        }
    }

    [Fact]
    public async Task StdioServer_SearchDefaultsToPlainAgentLines_AndJsonIsUnchanged()
    {
        var (server, session, _) = await CreateServerAsync("devx-mcp-stdio-agent");
        var id = session.Id.Value.ToString();
        var requests = string.Join("\n", [
            $"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{{\"name\":\"search\",\"arguments\":{{\"sessionId\":\"{id}\",\"kind\":\"symbol\",\"query\":\"Run\"}}}}}}",
            $"{{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{{\"name\":\"search\",\"arguments\":{{\"sessionId\":\"{id}\",\"kind\":\"symbol\",\"query\":\"Run\",\"format\":\"json\"}}}}}}"
        ]) + "\n";
        var output = new StringWriter();

        await new McpStdioServer(server).RunAsync(new StringReader(requests), output);

        var responses = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line)).ToArray();
        var agentText = responses[0].RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        var jsonText = responses[1].RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("method Sample.Program.Run", agentText);
        Assert.DoesNotContain("{\"success\"", agentText);
        Assert.Contains("\"success\":true", jsonText);
        Assert.Contains("\"data\"", jsonText);
        Assert.Equal(responses[0].RootElement.GetProperty("result").GetProperty("isError").GetBoolean(), responses[1].RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task ToolsList_DescriptionsShareOrderedAgentGuidance()
    {
        var (server, _, _) = await CreateServerAsync("devx-mcp-guidance");
        var tools = await server.ListToolsAsync();
        var guidance = BuildAgentGuidance(tools);

        Assert.True(guidance.Split('\n').Length <= 5);
        var searchIndex = guidance.IndexOf("search", StringComparison.Ordinal);
        var readIndex = guidance.IndexOf("read", searchIndex + 1, StringComparison.Ordinal);
        var replaceIndex = guidance.IndexOf("edit.replace", readIndex + 1, StringComparison.Ordinal);
        var testIndex = guidance.IndexOf("test", replaceIndex + 1, StringComparison.Ordinal);
        Assert.True(searchIndex >= 0 && readIndex > searchIndex && replaceIndex > readIndex && testIndex > replaceIndex, guidance);
        Assert.All(tools, tool => Assert.True(tool.InputSchema.GetProperty("properties").TryGetProperty("format", out _)));
    }

    private static string BuildAgentGuidance(IReadOnlyList<McpToolDescriptor> tools)
        => string.Join("\n", new[] { "search", "read", "edit.replace", "test" }
            .Select(name => tools.Single(tool => tool.Name == name).Description));

    [Fact]
    public async Task McpTools_DefaultSearchKindAndReadKindAndAcceptMultipleTargetsWithoutSessionId()
    {
        var (server, _, _) = await CreateServerAsync("devx-mcp-short-forms");
        var tools = await server.ListToolsAsync();
        foreach (var tool in tools)
        {
            using var schema = JsonDocument.Parse(tool.InputSchema.GetRawText());
            if (schema.RootElement.TryGetProperty("required", out var required))
                Assert.DoesNotContain("sessionId", required.EnumerateArray().Select(item => item.GetString()));
        }
        using var searchSchema = JsonDocument.Parse(tools.Single(tool => tool.Name == "search").InputSchema.GetRawText());
        Assert.Equal("query", searchSchema.RootElement.GetProperty("required")[0].GetString());
        Assert.DoesNotContain("sessionId", searchSchema.RootElement.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        using var readSchema = JsonDocument.Parse(tools.Single(tool => tool.Name == "read").InputSchema.GetRawText());
        Assert.DoesNotContain("sessionId", readSchema.RootElement.GetProperty("required").EnumerateArray().Select(item => item.GetString()));

        var search = await server.CallToolAsync("search", JsonSerializer.SerializeToElement(new { query = "Run" }));
        Assert.False(search.IsError);
        var bareRead = await server.CallToolAsync("read", JsonSerializer.SerializeToElement(new { target = "Run" }));
        Assert.False(bareRead.IsError);
        Assert.Contains("Run", bareRead.Content.GetRawText());
        var read = await server.CallToolAsync("read", JsonSerializer.SerializeToElement(new { targets = new[] { "src/Sample/Program.cs", "Run" } }));
        Assert.False(read.IsError);
        Assert.Contains("=== src/Sample/Program.cs ===", read.Content.GetProperty("text").GetString());
        Assert.Contains("=== Run ===", read.Content.GetProperty("text").GetString());
    }

    [Fact]
    public async Task StdioServer_SearchBeforeIndex_ReturnsIndexHint()
    {
        var (server, session, _) = await CreateServerAsync("devx-mcp-unindexed", indexRepository: false);
        var input = new StringReader($"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{{\"name\":\"search\",\"arguments\":{{\"sessionId\":\"{session.Id.Value}\",\"kind\":\"symbol\",\"query\":\"Program\"}}}}}}\n");
        var output = new StringWriter();

        await new McpStdioServer(server).RunAsync(input, output);

        using var response = JsonDocument.Parse(output.ToString());
        var result = response.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Contains("repository not indexed", result.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task StdioServer_PlainFolderIndexThenSearchFindsPythonFunction()
    {
        var (server, session, _) = await CreateServerAsync("devx-mcp-plain", indexRepository: false, plainFolder: true);
        var requests = string.Join("\n", [
            $"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{{\"name\":\"index\",\"arguments\":{{\"sessionId\":\"{session.Id.Value}\"}}}}}}",
            $"{{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{{\"name\":\"search\",\"arguments\":{{\"sessionId\":\"{session.Id.Value}\",\"kind\":\"symbol\",\"query\":\"mcpPlainFunction\"}}}}}}"
        ]) + "\n";
        var output = new StringWriter();

        await new McpStdioServer(server).RunAsync(new StringReader(requests), output);

        var responses = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line)).ToArray();
        Assert.Contains("filesIndexed", responses[0].RootElement.GetRawText());
        Assert.Contains("mcpPlainFunction", responses[1].RootElement.GetRawText());
    }

    [Fact]
    public async Task CallToolAsync_IndexWithoutFilesIndexesWholeRepository()
    {
        var (server, session, _) = await CreateServerAsync("devx-mcp-whole-index", indexRepository: false);
        // format:json is the only way to get the envelope now; without it every tool answers
        // the compact agent text. The payload shape is what this test is about.
        var indexed = await server.CallToolAsync("index", JsonSerializer.SerializeToElement(new { sessionId = session.Id.Value.ToString(), format = "json" }));

        Assert.False(indexed.IsError);
        Assert.True(indexed.Content.GetProperty("filesIndexed").GetInt32() > 0);
        var search = await server.CallToolAsync("search", JsonSerializer.SerializeToElement(new { sessionId = session.Id.Value.ToString(), kind = "symbol", query = "Program" }));
        Assert.False(search.IsError);
        Assert.Contains("Program", search.Content.GetRawText());
    }

    [Fact]
    public async Task CallToolAsync_ReadAcceptsCanonicalAndDeprecatedArgumentNames()
    {
        var (server, session, _) = await CreateServerAsync("devx-mcp-callers-alias");
        var canonical = await server.CallToolAsync("read", JsonSerializer.SerializeToElement(new { sessionId = session.Id.Value.ToString(), kind = "file", target = "src/Sample/Program.cs" }));
        var alias = await server.CallToolAsync("read", JsonSerializer.SerializeToElement(new { sessionId = session.Id.Value.ToString(), kind = "file", query = "src/Sample/Program.cs" }));

        Assert.False(canonical.IsError);
        Assert.False(alias.IsError);
    }

    [Fact]
    public async Task CallToolAsync_Index_IncrementallyIndexesFiles()
    {
        var (server, session, repositoryRoot) = await CreateServerAsync("devx-mcp-index");
        var path = Path.Combine(repositoryRoot, "src", "Sample", "Added.cs");
        await IOFile.WriteAllTextAsync(path, "namespace Sample; public sealed class AddedByMcp { }");

        var result = await server.CallToolAsync("index", JsonSerializer.SerializeToElement(new
        {
            sessionId = session.Id.Value.ToString(),
            files = new[] { "src/Sample/Added.cs" },
            format = "json"
        }));

        Assert.False(result.IsError);
        Assert.True(result.Content.GetProperty("success").GetBoolean());
        Assert.Equal(1, result.Content.GetProperty("result").GetProperty("updatedFiles").GetInt32());
        var search = await server.CallToolAsync("search", JsonSerializer.SerializeToElement(new
        {
            sessionId = session.Id.Value.ToString(),
            kind = "symbol",
            query = "AddedByMcp",
            format = "json"
        }));
        Assert.True(search.Content.GetProperty("success").GetBoolean());
        Assert.Contains("AddedByMcp", search.Content.GetRawText());
    }

    [Fact]
    public async Task CallToolAsync_PatchValidateApplyAndReport_ReturnSuccessfulPayloads()
    {
        var (server, session, repositoryRoot) = await CreateServerAsync("devx-mcp-patch");

        var patch = JsonSerializer.SerializeToElement(new
        {
            version = "1.0",
            operations = new[]
            {
                new
                {
                    operation = "CreateFile",
                    path = "src/Sample/Generated.cs",
                    content = "namespace Sample; public static class Generated { public static string Name() => \"mcp\"; }"
                }
            }
        });

        var validate = await server.CallToolAsync("patch.validate", JsonSerializer.SerializeToElement(new
        {
            sessionId = session.Id.Value.ToString(),
            patch,
            format = "json"
        }));

        Assert.False(validate.IsError);
        Assert.True(validate.Content.GetProperty("success").GetBoolean());

        var apply = await server.CallToolAsync("patch.apply", JsonSerializer.SerializeToElement(new
        {
            sessionId = session.Id.Value.ToString(),
            patch,
            format = "json"
        }));

        Assert.False(apply.IsError);
        Assert.True(apply.Content.GetProperty("success").GetBoolean());
        Assert.True(IOFile.Exists(Path.Combine(repositoryRoot, "src", "Sample", "Generated.cs")));

        var report = await server.CallToolAsync("report", JsonSerializer.SerializeToElement(new
        {
            sessionId = session.Id.Value.ToString(),
            format = "json"
        }));

        Assert.False(report.IsError);
        Assert.Equal(session.Id.Value.ToString(), report.Content.GetProperty("sessionSummary").GetProperty("sessionId").GetString());
        Assert.True(report.Content.GetProperty("operationLog").GetArrayLength() >= 0);
    }

    [Fact]
    public async Task StdioServer_InitializeListAndCallTool_RoundTripsJsonRpc()
    {
        var (server, session, _) = await CreateServerAsync("devx-mcp-stdio");
        var input = new StringReader(string.Join('\n',
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\"}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}",
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}",
            $"{{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{{\"name\":\"search\",\"arguments\":{{\"sessionId\":\"{session.Id.Value}\",\"kind\":\"symbol\",\"query\":\"Program\"}}}}}}"));
        var output = new StringWriter();

        await new McpStdioServer(server).RunAsync(input, output);

        var responses = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, responses.Length);
        using var initialize = JsonDocument.Parse(responses[0]);
        Assert.Equal("devx", initialize.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.Equal("1.6.5", initialize.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("version").GetString());
        using var list = JsonDocument.Parse(responses[1]);
        Assert.True(list.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength() > 0);
        using var call = JsonDocument.Parse(responses[2]);
        Assert.False(call.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task StdioServer_InvalidJsonAndUnknownMethod_ReturnJsonRpcErrors()
    {
        var (server, _, _) = await CreateServerAsync("devx-mcp-errors");
        var input = new StringReader("{broken\n{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"not/a/method\"}\n");
        var output = new StringWriter();

        await new McpStdioServer(server).RunAsync(input, output);

        var responses = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        using var parseError = JsonDocument.Parse(responses[0]);
        Assert.Equal(-32700, parseError.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        using var methodError = JsonDocument.Parse(responses[1]);
        Assert.Equal(-32601, methodError.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task StdioServer_PingAndToolCallFailures_ReturnExpectedResponses()
    {
        var (server, _, _) = await CreateServerAsync("devx-mcp-stdio-failures");
        var input = new StringReader(string.Join('\n',
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}",
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"no-such-tool\",\"arguments\":{}}}",
            "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"search\",\"arguments\":{}}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}"));
        var output = new StringWriter();

        await new McpStdioServer(server).RunAsync(input, output);

        var responses = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, responses.Length);
        using var ping = JsonDocument.Parse(responses[0]);
        Assert.Equal(1, ping.RootElement.GetProperty("id").GetInt32());
        Assert.True(ping.RootElement.GetProperty("result").ValueKind == JsonValueKind.Object);
        using var unknownTool = JsonDocument.Parse(responses[1]);
        Assert.True(unknownTool.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        using var missingArguments = JsonDocument.Parse(responses[2]);
        Assert.True(missingArguments.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    private static async Task<(McpReferenceServer Server, SessionState Session, string RepositoryRoot)> CreateServerAsync(string prefix, bool indexRepository = true, bool plainFolder = false, Func<string, VerifyAfterEdit>? verifyAfterEditFactory = null)
    {
        var repositoryRoot = Directory.CreateTempSubdirectory(prefix).FullName;
        var projectDirectory = Path.Combine(repositoryRoot, "src", "Sample");
        Directory.CreateDirectory(projectDirectory);

        if (!plainFolder) await IOFile.WriteAllTextAsync(Path.Combine(projectDirectory, "Sample.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        if (!plainFolder) await IOFile.WriteAllTextAsync(Path.Combine(projectDirectory, "Program.cs"), """
            namespace Sample;
            public static class Program
            {
                public static void Run() { }
            }
            """);
        if (plainFolder)
            await IOFile.WriteAllTextAsync(Path.Combine(repositoryRoot, "plain.py"), "def mcpPlainFunction():\n    return 1\n");

        var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"devx-mcp-{Guid.NewGuid():N}.db") }.ToString();
        var store = new SqliteStorageInitializer(connectionString);
        await store.InitializeAsync();
        var indexer = new RepositoryIndexingService(store);
        if (indexRepository)
            await indexer.IndexAsync(repositoryRoot);
        else
        {
            var repositoryHash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(repositoryRoot.ToLowerInvariant()));
            await store.UpsertRepositoryAsync(new Repository(new RepositoryId(new Guid(repositoryHash.AsSpan(0, 16))), Path.GetFileName(repositoryRoot), repositoryRoot));
        }

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
            verifyAfterEditFactory);
        return (server, session, repositoryRoot);
    }

    private static async Task<string> GetRepositoryIdAsync(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM repositories LIMIT 1;";
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
