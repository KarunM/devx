using DevX.Cli;
using DevX.Domain;
using DevX.Indexer;
using DevX.Infrastructure.Storage.SQLite;
using DevX.Protocol;
using Microsoft.Data.Sqlite;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Xunit;
using File = System.IO.File;

namespace DevX.Tests.Cli;

[CollectionDefinition("ProcessGlobalState", DisableParallelization = true)]
public sealed class ProcessGlobalStateCollection { }

[Collection("ProcessGlobalState")]
public sealed class CliCommandTests
{
    private static readonly SemaphoreSlim ProcessStateLock = new(1, 1);
    [Theory]
    // The version itself is asserted in Version_ReportsTheCliAssemblysVersion rather than
    // here: a hardcoded "1.6.5" left this suite red for the whole 1.6.6 release, and would
    // do the same for every release after it.
    [InlineData("--version", 0, "")]
    [InlineData("--help", 0, "1. devx search <name>")]
    [InlineData("unknown-command", 1, "Unknown command")]
    [InlineData("index", 1, "Missing repository path")]
    [InlineData("health", 1, "Status: Unavailable")]
    [InlineData("doctor", 1, "Status: Unavailable")]
    [InlineData("capabilities", 0, "")]
    [InlineData("search", 1, "Usage: devx search")]
    [InlineData("session", 1, "")]
    [InlineData("patch", 1, "")]
    [InlineData("artifact", 1, "")]
    [InlineData("build", 1, "")]
    [InlineData("test", 1, "")]
    [InlineData("diagnostics", 1, "")]
    [InlineData("run", 1, "")]
    [InlineData("fallback", 1, "")]
    [InlineData("snapshot", 1, "")]
    [InlineData("method", 1, "")]
    [InlineData("type", 1, "")]
    [InlineData("callers", 1, "")]
    [InlineData("references", 1, "")]
    [InlineData("read", 1, "")]
    [InlineData("callees", 1, "")]
    [InlineData("report", 1, "")]
    [InlineData("mcp", 1, "Usage")]
    public async Task Dispatch_InvokesCommandInTemporaryRepository(string command, int expectedExitCode, string outputFragment)
    {
        await ProcessStateLock.WaitAsync();
        var originalDirectory = Environment.CurrentDirectory;
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var root = Directory.CreateTempSubdirectory("devx-cli-dispatch").FullName;
        await File.WriteAllTextAsync(Path.Combine(root, "Sample.cs"), "namespace Sample; public sealed class SampleType { public void Run() { } }");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        try
        {
            Environment.CurrentDirectory = root;
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var entryPoint = typeof(ArtifactCommand).Assembly.EntryPoint!;
            var invocation = entryPoint.Invoke(null, [new[] { command }]);
            var exitCode = invocation is Task<int> task ? await task : Convert.ToInt32(invocation);
            Assert.Equal(expectedExitCode, exitCode);
            var output = stdout.ToString() + stderr.ToString();
            Assert.False(string.IsNullOrWhiteSpace(output), $"'{command}' produced no output.");
            if (!string.IsNullOrEmpty(outputFragment)) Assert.Contains(outputFragment, output, StringComparison.OrdinalIgnoreCase);
            if (command == "health") Assert.Contains("git: Degraded", output, StringComparison.OrdinalIgnoreCase);
            if (command == "doctor") Assert.Contains(".git directory", output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            Environment.CurrentDirectory = originalDirectory;
            Directory.Delete(root, recursive: true);
            ProcessStateLock.Release();
        }
    }

    [Fact]
    public async Task Version_ReportsTheCliAssemblysVersion()
    {
        // Reads the expected value from the assembly instead of hardcoding it: the literal
        // "1.6.5" sat here through the whole 1.6.6 release and left the suite red, which is
        // how a real regression hides. Whatever the csproj says, this test now agrees.
        var expected = typeof(ArtifactCommand).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(ArtifactCommand).Assembly.GetName().Version?.ToString()
            ?? "unknown";
        Assert.Matches(@"^\d+\.\d+\.\d+", expected);

        await ProcessStateLock.WaitAsync();
        var originalOut = Console.Out;
        var stdout = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            var entryPoint = typeof(ArtifactCommand).Assembly.EntryPoint!;
            var invocation = entryPoint.Invoke(null, [new[] { "--version" }]);
            if (invocation is Task<int> task) await task;
            Assert.Equal(expected, stdout.ToString().Trim());
        }
        finally
        {
            Console.SetOut(originalOut);
            ProcessStateLock.Release();
        }
    }

    [Fact]
    public async Task Snapshot_AcceptsSessionOptionBeforeOrAfterFile()
    {
        await ProcessStateLock.WaitAsync();
        var originalDirectory = Environment.CurrentDirectory;
        var root = Directory.CreateTempSubdirectory("devx-snapshot-order").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "Sample.cs"), "public sealed class Sample { }");
            Environment.CurrentDirectory = root;
            var (indexCode, _) = await InvokeCliAsync("index", ".");
            Assert.Equal(0, indexCode);
            var (sessionCode, sessionOutput) = await InvokeCliAsync("session", "create", "--format", "json");
            Assert.Equal(0, sessionCode);
            using var document = JsonDocument.Parse(sessionOutput);
            var sessionId = document.RootElement.GetProperty("Data").GetProperty("sessionId").GetString()!;

            var (fileFirstCode, fileFirst) = await InvokeCliAsync("snapshot", "Sample.cs", "--session", sessionId, "--format", "json");
            var (optionFirstCode, optionFirst) = await InvokeCliAsync("snapshot", "--session", sessionId, "Sample.cs", "--format", "json");

            Assert.Equal(0, fileFirstCode);
            Assert.Equal(0, optionFirstCode);
            using var firstDocument = JsonDocument.Parse(fileFirst);
            using var secondDocument = JsonDocument.Parse(optionFirst);
            Assert.Equal(firstDocument.RootElement.GetRawText(), secondDocument.RootElement.GetRawText());
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Directory.Delete(root, recursive: true);
            ProcessStateLock.Release();
        }
    }

    [Fact]
    public async Task SearchReadAndSnapshot_AgentFormatPrintCompactText()
    {
        await ProcessStateLock.WaitAsync();
        var originalDirectory = Environment.CurrentDirectory;
        var root = Directory.CreateTempSubdirectory("devx-agent-format").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            await File.WriteAllTextAsync(Path.Combine(root, "Sample.cs"), "public sealed class AgentFixtureOne { public void RunOne() { } }\npublic sealed class AgentFixtureTwo { public void RunTwo() { } }\npublic sealed class AgentFixtureThree { public void RunThree() { } }");
            await File.WriteAllTextAsync(Path.Combine(root, "First.cs"), "public sealed class First { public void Run() { } }");
            await File.WriteAllTextAsync(Path.Combine(root, "Second.cs"), "public sealed class Second { public void Run() { } }");
            await File.WriteAllTextAsync(Path.Combine(root, "notes.md"), "UniquePreview phrase for text search.");
            Environment.CurrentDirectory = root;
            var (searchCode, search) = await InvokeCliAsync("search", "AgentFixture", "--format", "agent");
            var (searchJsonCode, searchJson) = await InvokeCliAsync("search", "AgentFixture", "--format", "json");
            var (readCode, read) = await InvokeCliAsync("read", "AgentFixtureOne.RunOne", "--format", "agent");
            var (snapshotCode, snapshot) = await InvokeCliAsync("snapshot", "Sample.cs", "--format", "agent");
            var (snapshotJsonCode, snapshotJson) = await InvokeCliAsync("snapshot", "Sample.cs", "--format", "json");
            var (textCode, textSearch) = await InvokeCliAsync("search", "text", "UniquePreview", "--format", "agent");
            var (errorCode, error) = await InvokeCliAsync("snapshot", "Missing.cs", "--format", "agent");
            var (ambiguousCode, ambiguous) = await InvokeCliAsync("read", "Run", "--format", "agent");
            Assert.Equal(0, searchCode); Assert.Equal(0, readCode); Assert.Equal(0, snapshotCode);
            Assert.Equal(0, searchJsonCode); Assert.Equal(0, snapshotJsonCode);
            Assert.Equal(0, textCode); Assert.Equal(1, errorCode);
            Assert.Equal(2, ambiguousCode);
            var expectedSearchLines = new[]
            {
                "class AgentFixtureOne",
                "method AgentFixtureOne.RunOne",
                "class AgentFixtureTwo",
                "method AgentFixtureTwo.RunTwo",
                "class AgentFixtureThree",
                "method AgentFixtureThree.RunThree"
            };
            var actualSearchLines = search.Trim().Split('\n')
                .Where(line => !line.StartsWith("(indexed ", StringComparison.Ordinal));
            Assert.Equal(expectedSearchLines.Length, actualSearchLines.Count());
            Assert.All(expectedSearchLines, fragment => Assert.Contains(fragment, search));
            Assert.Contains("Sample.cs:", read);
            Assert.Contains("[method]", read);
            Assert.Contains("public void RunOne()", read);
            Assert.Contains("Sample.cs (C#", snapshot);
            Assert.Contains("type AgentFixtureOne", snapshot);
            Assert.Contains("method RunOne", snapshot);
            Assert.Contains("notes.md:1:", textSearch);
            Assert.Contains("UniquePreview phrase", textSearch);
            Assert.Contains("error: File 'Missing.cs'", error);
            Assert.Contains("ambiguous: Run matches 2 symbols", ambiguous);
            Assert.Contains("First.Run", ambiguous);
            Assert.Contains("Second.Run", ambiguous);
            Assert.DoesNotContain("Id", snapshot, StringComparison.OrdinalIgnoreCase);
            var searchAgentBytes = Encoding.UTF8.GetByteCount(search);
            var searchJsonBytes = Encoding.UTF8.GetByteCount(searchJson);
            var snapshotAgentBytes = Encoding.UTF8.GetByteCount(snapshot);
            var snapshotJsonBytes = Encoding.UTF8.GetByteCount(snapshotJson);
            Assert.True(searchAgentBytes <= searchJsonBytes / 2, $"Search agent={searchAgentBytes} JSON={searchJsonBytes}");
            Assert.True(snapshotAgentBytes <= snapshotJsonBytes / 2, $"Snapshot agent={snapshotAgentBytes} JSON={snapshotJsonBytes}");
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Directory.Delete(root, recursive: true);
            ProcessStateLock.Release();
        }
    }

    [Fact]
    public async Task HelpStartsWithAgentQuickStart()
    {
        var (_, output) = await InvokeCliAsync("--help");
        Assert.StartsWith("1. devx search <name>   (symbols; falls back to text)\n2. devx read <name> [more names]   (source of a symbol, file, or file#Heading)\n3. devx replace <file> --old <text> --new <text>   (edit; add --all or --occurrence n)\n4. devx test [file|dir]   (build+test summary)\nAdd --format agent for compact output; no session needed.", output);
    }

    [Fact]
    public async Task AutoSession_ReusesPerRepositorySessionAndReplacesStaleFile()
    {
        await ProcessStateLock.WaitAsync();
        var originalDirectory = Environment.CurrentDirectory;
        var root = Directory.CreateTempSubdirectory("devx-auto-session").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            await File.WriteAllTextAsync(Path.Combine(root, "Sample.cs"), "public sealed class Sample { public void NormalizeName() { } }");
            Environment.CurrentDirectory = root;
            var (firstCode, firstOutput) = await InvokeCliAsync("search", "NormalizeName");
            Assert.Equal(0, firstCode);
            Assert.Contains("(indexed", firstOutput);
            var sessionPath = Path.Combine(root, ".devx", "auto-session");
            var firstSession = await File.ReadAllTextAsync(sessionPath);
            var store = new SqliteStorageInitializer(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, ".devx", "devx.db") }.ToString());
            await store.InitializeAsync();
            var repositoryHash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(root.ToLowerInvariant()));
            var repository = new Repository(new RepositoryId(new Guid(repositoryHash.AsSpan(0, 16))), Path.GetFileName(root), root);
            var mcpSession = await AutomaticSessionResolver.GetOrCreateAsync(store, root, repository);
            var engine = new ProtocolEngine(new SearchService(store, store), store, store);
            var mcpOne = new McpReferenceServer(engine, store, store, mcpSession.Id.Value.ToString());
            var mcpTwo = new McpReferenceServer(engine, store, store, mcpSession.Id.Value.ToString());
            var mcpOneReport = await mcpOne.CallToolAsync("report", JsonSerializer.SerializeToElement(new { }));
            var mcpTwoReport = await mcpTwo.CallToolAsync("report", JsonSerializer.SerializeToElement(new { }));
            Assert.Contains(firstSession.Trim(), mcpOneReport.Content.GetRawText());
            Assert.Contains(firstSession.Trim(), mcpTwoReport.Content.GetRawText());
            var (secondCode, _) = await InvokeCliAsync("search", "NormalizeName");
            Assert.Equal(0, secondCode);
            Assert.Equal(firstSession, await File.ReadAllTextAsync(sessionPath));
            await File.WriteAllTextAsync(sessionPath, "stale");
            var (staleCode, _) = await InvokeCliAsync("search", "NormalizeName");
            Assert.Equal(0, staleCode);
            Assert.NotEqual("stale", await File.ReadAllTextAsync(sessionPath));
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Directory.Delete(root, recursive: true);
            ProcessStateLock.Release();
        }
    }

    [Fact]
    public async Task Read_ShortFileSymbolAndMultiTargetFormsWorkWithoutExplicitSession()
    {
        await ProcessStateLock.WaitAsync();
        var originalDirectory = Environment.CurrentDirectory;
        var root = Directory.CreateTempSubdirectory("devx-short-read").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            await File.WriteAllTextAsync(Path.Combine(root, "a.py"), "def normalize_name():\n    return 'normalized'\n\ndef other():\n    return 'other'\n");
            await File.WriteAllTextAsync(Path.Combine(root, "b.py"), "def run():\n    return 'first'\n");
            await File.WriteAllTextAsync(Path.Combine(root, "c.py"), "def run():\n    return 'second'\n");
            await File.WriteAllTextAsync(Path.Combine(root, "README.md"), "# Guide\n\n## Usage\n\nDistinctive fallback phrase.\n");
            await File.WriteAllTextAsync(Path.Combine(root, "replace.txt"), "before");
            Environment.CurrentDirectory = root;
            var (symbolCode, symbolOutput) = await InvokeCliAsync("read", "normalize_name");
            Assert.Equal(0, symbolCode);
            Assert.Contains("normalized", symbolOutput);
            var (fileCode, fileOutput) = await InvokeCliAsync("read", "a.py");
            Assert.Equal(0, fileCode);
            Assert.Contains("def normalize_name", fileOutput);
            var (sectionCode, sectionOutput) = await InvokeCliAsync("read", "README.md#Usage");
            Assert.Equal(0, sectionCode);
            Assert.Contains("Distinctive fallback phrase", sectionOutput);
            var (multiSuccessCode, multiSuccessOutput) = await InvokeCliAsync("read", "normalize_name", "other");
            Assert.Equal(0, multiSuccessCode);
            Assert.Contains("=== normalize_name ===", multiSuccessOutput);
            Assert.Contains("=== other ===", multiSuccessOutput);
            var (multiCode, multiOutput) = await InvokeCliAsync("read", "normalize_name", "MissingName");
            Assert.Equal(2, multiCode);
            Assert.Contains("=== normalize_name ===", multiOutput);
            Assert.Contains("=== MissingName ===", multiOutput);
            Assert.True(multiOutput.IndexOf("=== normalize_name ===", StringComparison.Ordinal) < multiOutput.IndexOf("=== MissingName ===", StringComparison.Ordinal));
            var (ambiguousCode, ambiguousOutput) = await InvokeCliAsync("read", "run");
            Assert.Equal(2, ambiguousCode);
            Assert.Contains("ambiguous: run matches 2 symbols", ambiguousOutput);
            Assert.Contains("b.<module>.run", ambiguousOutput);
            var (fallbackCode, fallbackOutput) = await InvokeCliAsync("search", "Distinctive fallback phrase");
            Assert.Equal(0, fallbackCode);
            Assert.Contains("no symbol match; showing text matches", fallbackOutput);
            var (replaceCode, _) = await InvokeCliAsync("replace", "replace.txt", "--old", "before", "--new", "after");
            Assert.Equal(0, replaceCode);
            Assert.Equal("after", await File.ReadAllTextAsync(Path.Combine(root, "replace.txt")));
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Directory.Delete(root, recursive: true);
            ProcessStateLock.Release();
        }
    }

    private static async Task<(int ExitCode, string Output)> InvokeCliAsync(params string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var entryPoint = typeof(ArtifactCommand).Assembly.EntryPoint!;
            var invocation = entryPoint.Invoke(null, [args]);
            var exitCode = invocation is Task<int> task ? await task : Convert.ToInt32(invocation);
            return (exitCode, stdout.ToString() + stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}
