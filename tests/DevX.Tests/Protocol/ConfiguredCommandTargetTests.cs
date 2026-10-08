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
/// A configured test or build command is the repository answering for itself, so it applies to
/// every call whatever the target form: bare, a path, or a runner label. Before this,
/// `devx test tests/x.py` fell through to discovery and never read `.devx/config.json`, which
/// made the config the benchmark hands arm B inert for six of its eight measurable tasks.
/// A repository that declares a command and whose runtime discovery finds nothing must still
/// run -- otherwise a documentation tree cannot be built or tested at all.
/// </summary>
[Collection("ProcessGlobalState")]
public sealed class ConfiguredCommandTargetTests
{
    private const string TestBlock =
        """{ "command": "python3", "args": ["-c", "import sys; print('CONFIG-TARGET', sys.argv[1:])", "{target}"] }""";

    [Fact]
    public async Task BareTestCall_RunsTheConfiguredCommand_WithAnEmptyTarget()
    {
        var (engine, session, _) = await CreateEngineAsync("devx-config-bare", TestBlock, null);

        var response = await ExecuteAsync(engine, session, ProtocolOperations.RunTests, new { });

        Assert.True(response.Success, response.Errors.FirstOrDefault()?.Message);
        var payload = response.Data!.Value.Deserialize<ValidationExecutionResponse>()!;
        Assert.Contains("CONFIG-TARGET", payload.Execution.Command);
        // `{target}` is substituted with nothing when the caller named no target. The discovered
        // project path must not be substituted in its place: on Django that runs `runtests.py .`,
        // which is not a label and fails.
        Assert.EndsWith("[]", IOFile.ReadAllText(payload.Execution.RawOutput).Trim());
        Assert.DoesNotContain("-m pytest", payload.Execution.Command);
    }

    [Fact]
    public async Task PathTargetTestCall_RunsTheConfiguredCommand_NotDiscovery()
    {
        var (engine, session, root) = await CreateEngineAsync("devx-config-path", TestBlock, null);

        var response = await ExecuteAsync(engine, session, ProtocolOperations.RunTests,
            new { targets = new[] { "tests/test_thing.py" } });

        Assert.True(response.Success, response.Errors.FirstOrDefault()?.Message);
        var payload = response.Data!.Value.Deserialize<ValidationExecutionResponse>()!;
        Assert.Contains("CONFIG-TARGET", payload.Execution.Command);
        Assert.Contains("tests/test_thing.py", payload.Execution.Command);
        // A path target is what a .py file usually is, and it used to bypass the config entirely.
        Assert.DoesNotContain("-m pytest", payload.Execution.Command);
        Assert.True(IOFile.Exists(Path.Combine(root, "tests", "test_thing.py")));
    }

    [Fact]
    public async Task LabelTargetTestCall_RunsTheConfiguredCommand_WithTheLabel()
    {
        var (engine, session, _) = await CreateEngineAsync("devx-config-label", TestBlock, null);

        var response = await ExecuteAsync(engine, session, ProtocolOperations.RunTests,
            new { targets = new[] { "queries.tests.QueriesTests" } });

        Assert.True(response.Success, response.Errors.FirstOrDefault()?.Message);
        var payload = response.Data!.Value.Deserialize<ValidationExecutionResponse>()!;
        Assert.Contains("CONFIG-TARGET", payload.Execution.Command);
        Assert.Contains("queries.tests.QueriesTests", payload.Execution.Command);
    }

    [Fact]
    public async Task BuildCall_UsesTheConfiguredCommand_WhenDiscoveryFoundSomething()
    {
        var (engine, session, _) = await CreateEngineAsync("devx-config-build",
            TestBlock, """{ "command": "python3", "args": ["-c", "print('CONFIG-BUILD')"] }""");

        var response = await ExecuteAsync(engine, session, ProtocolOperations.RunBuild, new { });

        Assert.True(response.Success, response.Errors.FirstOrDefault()?.Message);
        var payload = response.Data!.Value.Deserialize<ValidationExecutionResponse>()!;
        Assert.Contains("CONFIG-BUILD", payload.Execution.Command);
        Assert.DoesNotContain("compileall", payload.Execution.Command);
    }

    [Fact]
    public async Task BuildCall_StillRuns_WhenDiscoveryFindsNothing()
    {
        // No .sln, .csproj, package.json or Python project: discovery finds nothing, and the
        // repository's own build command is the only answer there is. Refusing here is what
        // sent an agent to Bash.
        var (engine, session, _) = await CreateEngineAsync("devx-config-build-only",
            TestBlock, """{ "command": "python3", "args": ["-c", "print('CONFIG-BUILD')"] }""",
            withRuntimeFile: false);

        var response = await ExecuteAsync(engine, session, ProtocolOperations.RunBuild, new { });

        Assert.True(response.Success, response.Errors.FirstOrDefault()?.Message);
        var payload = response.Data!.Value.Deserialize<ValidationExecutionResponse>()!;
        Assert.Contains("CONFIG-BUILD", payload.Execution.Command);
        Assert.Equal("configured", payload.Target.RuntimeKind);
    }

    private static Task<ProtocolResponse> ExecuteAsync(
        ProtocolEngine engine, SessionState session, string operation, object payload)
        => engine.ExecuteAsync(new ProtocolRequest("1.0", operation, session.Id.Value.ToString(),
            JsonSerializer.SerializeToElement(payload)));

    private static async Task<(ProtocolEngine Engine, SessionState Session, string Root)> CreateEngineAsync(
        string prefix, string testBlock, string? buildBlock, bool withRuntimeFile = true)
    {
        var root = Directory.CreateTempSubdirectory(prefix).FullName;
        Directory.CreateDirectory(Path.Combine(root, ".devx"));
        Directory.CreateDirectory(Path.Combine(root, "tests"));
        if (withRuntimeFile)
        {
            IOFile.WriteAllText(Path.Combine(root, "tests", "test_thing.py"), "def test_thing():\n    assert True\n");
        }
        var blocks = buildBlock is null ? testBlock : $"{testBlock}, \"build\": {buildBlock}";
        IOFile.WriteAllText(Path.Combine(root, ".devx", "config.json"), $"{{\"test\": {blocks}}}");

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(Path.GetTempPath(), $"devx-config-target-{Guid.NewGuid():N}.db")
        }.ToString();
        var store = new SqliteStorageInitializer(connectionString);
        await new RepositoryIndexingService(store).IndexAsync(root);

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM repositories LIMIT 1;";
        var repositoryId = (string)(await command.ExecuteScalarAsync())!;

        var session = await store.CreateSessionAsync(new CreateSessionRequest(new RepositoryId(Guid.Parse(repositoryId))));
        var engine = new ProtocolEngine(new SearchService(store, store), store, store);
        return (engine, session, root);
    }
}
