using System.Text.Json;
using DevX.Domain;
using DevX.Infrastructure.Storage.SQLite;
using DevX.Indexer;
using DevX.Protocol;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevX.Tests.Protocol;

public sealed class ProtocolEngineTests
{
    [Fact]
    public async Task ExecuteAsync_SearchSymbol_UpdatesSessionAndReturnsMetadataOnly()
    {
        var repositoryRoot = Directory.CreateTempSubdirectory("devx-protocol-test").FullName;
        var projectDirectory = Path.Combine(repositoryRoot, "src", "Sample");
        Directory.CreateDirectory(projectDirectory);

        await System.IO.File.WriteAllTextAsync(Path.Combine(projectDirectory, "Sample.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        await System.IO.File.WriteAllTextAsync(Path.Combine(projectDirectory, "Alpha.cs"), """
            namespace Sample;
            public class Alpha { public void Run() { } }
            """);

        var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"devx-protocol-{Guid.NewGuid():N}.db") }.ToString();
        var store = new SqliteStorageInitializer(connectionString);
        var indexer = new RepositoryIndexingService(store);
        await indexer.IndexAsync(repositoryRoot);

        var repositoryId = new RepositoryId(Guid.NewGuid());
        await store.UpsertRepositoryAsync(new Repository(repositoryId, "sample", repositoryRoot));
        var session = await store.CreateSessionAsync(new CreateSessionRequest(repositoryId));

        var engine = new ProtocolEngine(new SearchService(store, store), store, store);
        var request = new ProtocolRequest(
            "1.0",
            ProtocolOperations.SearchSymbol,
            session.Id.Value.ToString(),
            JsonSerializer.SerializeToElement(new { query = "Run" }));

        var response = await engine.ExecuteAsync(request);

        Assert.True(response.Success);
        Assert.Empty(response.Errors);
        Assert.NotNull(response.Data);
        Assert.DoesNotContain("public class Alpha", response.Data!.Value.GetRawText(), StringComparison.OrdinalIgnoreCase);

        var resumed = await store.GetSessionAsync(new GetSessionRequest(session.Id));
        Assert.NotNull(resumed);
        Assert.Contains("Run", resumed!.SearchHistory);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownOperation_ReturnsStructuredError()
    {
        var store = new SqliteStorageInitializer(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"devx-protocol-{Guid.NewGuid():N}.db") }.ToString());
        await store.InitializeAsync();
        var repositoryId = new RepositoryId(Guid.NewGuid());
        await store.UpsertRepositoryAsync(new Repository(repositoryId, "sample", "/tmp/sample"));
        var session = await store.CreateSessionAsync(new CreateSessionRequest(repositoryId));

        var engine = new ProtocolEngine(new SearchService(store, store), store, store);
        var response = await engine.ExecuteAsync(new ProtocolRequest("1.0", "NOPE", session.Id.Value.ToString(), JsonSerializer.SerializeToElement(new { })));

        Assert.False(response.Success);
        Assert.Contains(response.Errors, error => error.Code == "InvalidOperation");
    }

    [Fact]
    public async Task ExecuteAsync_GetMethodSummary_ReturnsMetadataOnlyAndMarksSession()
    {
        var repositoryRoot = Directory.CreateTempSubdirectory("devx-method-summary-test").FullName;
        var projectDirectory = Path.Combine(repositoryRoot, "src", "Sample");
        Directory.CreateDirectory(projectDirectory);

        await System.IO.File.WriteAllTextAsync(Path.Combine(projectDirectory, "Sample.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        await System.IO.File.WriteAllTextAsync(Path.Combine(projectDirectory, "Alpha.cs"), """
            namespace Sample;
            public class Alpha
            {
                public void Run() { }
            }
            """);

        await System.IO.File.WriteAllTextAsync(Path.Combine(projectDirectory, "Beta.cs"), """
            namespace Sample;
            public class Beta
            {
                public void Run() { }
            }
            """);

        var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"devx-method-{Guid.NewGuid():N}.db") }.ToString();
        var store = new SqliteStorageInitializer(connectionString);
        var indexer = new RepositoryIndexingService(store);
        await indexer.IndexAsync(repositoryRoot);

        var repositoryId = new RepositoryId(Guid.NewGuid());
        await store.UpsertRepositoryAsync(new Repository(repositoryId, "sample", repositoryRoot));
        var session = await store.CreateSessionAsync(new CreateSessionRequest(repositoryId));

        var engine = new ProtocolEngine(new SearchService(store, store), store, store);
        var response = await engine.ExecuteAsync(new ProtocolRequest(
            "1.0",
            ProtocolOperations.GetMethodSummary,
            session.Id.Value.ToString(),
            JsonSerializer.SerializeToElement(new { query = "Sample.Alpha.Run" })));

        Assert.True(response.Success);
        Assert.NotNull(response.Data);
        Assert.DoesNotContain("public void Run()", response.Data!.Value.GetRawText(), StringComparison.OrdinalIgnoreCase);

        var payload = response.Data!.Value.Deserialize<MethodSummaryResponse>();
        Assert.NotNull(payload);
        Assert.Equal("Exact", payload!.MatchKind);
        Assert.NotNull(payload.SelectedMethod);
        Assert.Single(payload.Methods);
        Assert.Equal("Alpha.cs", Path.GetFileName(payload.SelectedMethod!.FilePath));
        Assert.DoesNotContain("Signature", response.Data!.Value.GetRawText(), StringComparison.OrdinalIgnoreCase);

        var resumed = await store.GetSessionAsync(new GetSessionRequest(session.Id));
        Assert.NotNull(resumed);
        Assert.Contains(payload.SelectedMethod.Id, resumed!.WorkingSet);
        Assert.Contains(payload.SelectedMethod.Id, resumed.ExpandedSymbols);
    }

    [Fact]
    public async Task ExecuteAsync_GetMethodSummary_ReturnsAmbiguousMatchForOverloads()
    {
        var repositoryRoot = Directory.CreateTempSubdirectory("devx-method-summary-ambiguous").FullName;
        var projectDirectory = Path.Combine(repositoryRoot, "src", "Sample");
        Directory.CreateDirectory(projectDirectory);

        await System.IO.File.WriteAllTextAsync(Path.Combine(projectDirectory, "Sample.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        await System.IO.File.WriteAllTextAsync(Path.Combine(projectDirectory, "Alpha.cs"), """
            namespace Sample;
            public class Alpha
            {
                public void Run() { }
            }
            """);

        await System.IO.File.WriteAllTextAsync(Path.Combine(projectDirectory, "Beta.cs"), """
            namespace Sample;
            public class Beta
            {
                public void Run() { }
            }
            """);

        var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"devx-method-ambiguous-{Guid.NewGuid():N}.db") }.ToString();
        var store = new SqliteStorageInitializer(connectionString);
        var indexer = new RepositoryIndexingService(store);
        await indexer.IndexAsync(repositoryRoot);

        var repositoryId = new RepositoryId(Guid.NewGuid());
        await store.UpsertRepositoryAsync(new Repository(repositoryId, "sample", repositoryRoot));
        var session = await store.CreateSessionAsync(new CreateSessionRequest(repositoryId));

        var engine = new ProtocolEngine(new SearchService(store, store), store, store);
        var response = await engine.ExecuteAsync(new ProtocolRequest(
            "1.0",
            ProtocolOperations.GetMethodSummary,
            session.Id.Value.ToString(),
            JsonSerializer.SerializeToElement(new { query = "Run" })));

        Assert.True(response.Success);
        var payload = response.Data!.Value.Deserialize<MethodSummaryResponse>();
        Assert.NotNull(payload);
        Assert.Equal("Ambiguous", payload!.MatchKind);
        Assert.Null(payload.SelectedMethod);
        Assert.True(payload.Methods.Count >= 2);
    }

    [Fact]
    public async Task ExecuteAsync_GetMethodSummary_ReturnsNotFoundForMissingMethod()
    {
        var store = new SqliteStorageInitializer(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"devx-method-missing-{Guid.NewGuid():N}.db") }.ToString());
        await store.InitializeAsync();
        var repositoryId = new RepositoryId(Guid.NewGuid());
        await store.UpsertRepositoryAsync(new Repository(repositoryId, "sample", "/tmp/sample"));
        var session = await store.CreateSessionAsync(new CreateSessionRequest(repositoryId));

        var engine = new ProtocolEngine(new SearchService(store, store), store, store);
        var response = await engine.ExecuteAsync(new ProtocolRequest("1.0", ProtocolOperations.GetMethodSummary, session.Id.Value.ToString(), JsonSerializer.SerializeToElement(new { query = "Missing" })));

        Assert.False(response.Success);
        Assert.Contains(response.Errors, error => error.Code == "NotFound");
    }

    [Fact]
    public async Task ExecuteAsync_GetMethod_ReturnsSingleImplementationOnly()
    {
        var repositoryRoot = Directory.CreateTempSubdirectory("devx-method-get").FullName;
        var projectDirectory = Path.Combine(repositoryRoot, "src", "Sample");
        Directory.CreateDirectory(projectDirectory);

        await System.IO.File.WriteAllTextAsync(Path.Combine(projectDirectory, "Sample.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        await System.IO.File.WriteAllTextAsync(Path.Combine(projectDirectory, "Alpha.cs"), """
            namespace Sample;
            public class Alpha
            {
                public void Run() { }
                public void Stop() { }
            }
            """);

        var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"devx-method-get-{Guid.NewGuid():N}.db") }.ToString();
        var store = new SqliteStorageInitializer(connectionString);
        var indexer = new RepositoryIndexingService(store);
        await indexer.IndexAsync(repositoryRoot);

        var repositoryId = new RepositoryId(Guid.NewGuid());
        await store.UpsertRepositoryAsync(new Repository(repositoryId, "sample", repositoryRoot));
        var session = await store.CreateSessionAsync(new CreateSessionRequest(repositoryId));

        var engine = new ProtocolEngine(new SearchService(store, store), store, store);
        var response = await engine.ExecuteAsync(new ProtocolRequest(
            "1.0",
            ProtocolOperations.GetMethod,
            session.Id.Value.ToString(),
            JsonSerializer.SerializeToElement(new { query = "Sample.Alpha.Run" })));

        Assert.True(response.Success);
        var payload = response.Data!.Value.Deserialize<MethodResponse>();
        Assert.NotNull(payload);
        Assert.Contains("public void Run()", payload!.Method.Source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Stop()", payload.Method.Source, StringComparison.OrdinalIgnoreCase);

        var resumed = await store.GetSessionAsync(new GetSessionRequest(session.Id));
        Assert.NotNull(resumed);
        Assert.Contains(payload.Method.Id, resumed!.WorkingSet);
        Assert.Contains(payload.Method.Id, resumed.ExpandedSymbols);
    }

    [Fact]
    public async Task ExecuteAsync_ReadMethod_ReturnsOnlyMethodLinesAndAccurateCounts()
    {
        var repositoryRoot = Directory.CreateTempSubdirectory("devx-read-method").FullName;
        var projectDirectory = Path.Combine(repositoryRoot, "src", "Sample");
        Directory.CreateDirectory(projectDirectory);

        await System.IO.File.WriteAllTextAsync(Path.Combine(projectDirectory, "Sample.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        await System.IO.File.WriteAllTextAsync(Path.Combine(projectDirectory, "Alpha.cs"), """
            namespace Sample;

            public class Alpha
            {
                public void Run()
                {
                    var value = 1;
                    Console.WriteLine(value);
                }

                public void Stop() { }
            }
            """);

        var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"devx-read-method-{Guid.NewGuid():N}.db") }.ToString();
        var store = new SqliteStorageInitializer(connectionString);
        await new RepositoryIndexingService(store).IndexAsync(repositoryRoot);

        var repositoryId = new RepositoryId(Guid.NewGuid());
        await store.UpsertRepositoryAsync(new Repository(repositoryId, "sample", repositoryRoot));
        var session = await store.CreateSessionAsync(new CreateSessionRequest(repositoryId));
        var engine = new ProtocolEngine(new SearchService(store, store), store, store);

        var response = await engine.ExecuteAsync(new ProtocolRequest(
            "1.0",
            "READ_METHOD",
            session.Id.Value.ToString(),
            JsonSerializer.SerializeToElement(new { query = "Sample.Alpha.Run" })));

        Assert.True(response.Success);
        var payload = response.Data!.Value.Deserialize<SourceReadResponse>();
        Assert.NotNull(payload);
        Assert.Equal(payload!.ReturnedLines, payload.Source.Split('\n').Length);
        Assert.True(payload.ReturnedLines < payload.TotalLines);
        Assert.True(payload.Truncated);
        Assert.DoesNotContain("Stop()", payload.Source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_GetMethod_ReturnsAmbiguousMatch()
    {
        var repositoryRoot = Directory.CreateTempSubdirectory("devx-method-get-ambiguous").FullName;
        var projectDirectory = Path.Combine(repositoryRoot, "src", "Sample");
        Directory.CreateDirectory(projectDirectory);

        await System.IO.File.WriteAllTextAsync(Path.Combine(projectDirectory, "Sample.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        await System.IO.File.WriteAllTextAsync(Path.Combine(projectDirectory, "Alpha.cs"), """
            namespace Sample;
            public class Alpha
            {
                public void Run() { }
            }
            """);

        await System.IO.File.WriteAllTextAsync(Path.Combine(projectDirectory, "Beta.cs"), """
            namespace Sample;
            public class Beta
            {
                public void Run() { }
            }
            """);

        var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"devx-method-get-ambiguous-{Guid.NewGuid():N}.db") }.ToString();
        var store = new SqliteStorageInitializer(connectionString);
        var indexer = new RepositoryIndexingService(store);
        await indexer.IndexAsync(repositoryRoot);

        var repositoryId = new RepositoryId(Guid.NewGuid());
        await store.UpsertRepositoryAsync(new Repository(repositoryId, "sample", repositoryRoot));
        var session = await store.CreateSessionAsync(new CreateSessionRequest(repositoryId));

        var engine = new ProtocolEngine(new SearchService(store, store), store, store);
        var response = await engine.ExecuteAsync(new ProtocolRequest("1.0", ProtocolOperations.GetMethod, session.Id.Value.ToString(), JsonSerializer.SerializeToElement(new { query = "Run" })));

        Assert.False(response.Success);
        Assert.Contains(response.Errors, error => error.Code == "AmbiguousMatch");
    }

    [Fact]
    public async Task ExecuteAsync_GetType_ReturnsMetadataOnly()
    {
        var repositoryRoot = Directory.CreateTempSubdirectory("devx-type-get").FullName;
        var projectDirectory = Path.Combine(repositoryRoot, "src", "Sample");
        Directory.CreateDirectory(projectDirectory);

        await System.IO.File.WriteAllTextAsync(Path.Combine(projectDirectory, "Sample.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        await System.IO.File.WriteAllTextAsync(Path.Combine(projectDirectory, "Alpha.cs"), """
            namespace Sample;
            public class Alpha
            {
                public void Run() { }
                public int Value { get; }
            }
            """);

        var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"devx-type-{Guid.NewGuid():N}.db") }.ToString();
        var store = new SqliteStorageInitializer(connectionString);
        var indexer = new RepositoryIndexingService(store);
        await indexer.IndexAsync(repositoryRoot);

        var repositoryId = new RepositoryId(Guid.NewGuid());
        await store.UpsertRepositoryAsync(new Repository(repositoryId, "sample", repositoryRoot));
        var session = await store.CreateSessionAsync(new CreateSessionRequest(repositoryId));

        var engine = new ProtocolEngine(new SearchService(store, store), store, store);
        var response = await engine.ExecuteAsync(new ProtocolRequest(
            "1.0",
            ProtocolOperations.GetTypeOperation,
            session.Id.Value.ToString(),
            JsonSerializer.SerializeToElement(new { query = "Sample.Alpha" })));

        Assert.True(response.Success);
        var payload = response.Data!.Value.Deserialize<TypeResponse>();
        Assert.NotNull(payload);
        Assert.Equal("Alpha", payload!.Type.Name);
        Assert.Equal("Sample.Alpha", payload.Type.QualifiedName);
        Assert.DoesNotContain("public void Run()", response.Data!.Value.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("class Alpha", response.Data!.Value.GetRawText(), StringComparison.OrdinalIgnoreCase);

        var resumed = await store.GetSessionAsync(new GetSessionRequest(session.Id));
        Assert.NotNull(resumed);
        Assert.Contains(payload.Type.Id, resumed!.WorkingSet);
        Assert.Contains(payload.Type.Id, resumed.ExpandedSymbols);
    }

    [Fact]
    public async Task ExecuteAsync_GetType_ReturnsAmbiguousMatch()
    {
        var repositoryRoot = Directory.CreateTempSubdirectory("devx-type-get-ambiguous").FullName;
        var projectDirectory = Path.Combine(repositoryRoot, "src", "Sample");
        Directory.CreateDirectory(projectDirectory);

        await System.IO.File.WriteAllTextAsync(Path.Combine(projectDirectory, "Sample.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        await System.IO.File.WriteAllTextAsync(Path.Combine(projectDirectory, "Alpha.cs"), """
            namespace Sample;
            public class Alpha { }
            """);

        await System.IO.File.WriteAllTextAsync(Path.Combine(projectDirectory, "Beta.cs"), """
            namespace Sample;
            public class Alpha { }
            """);

        var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"devx-type-ambiguous-{Guid.NewGuid():N}.db") }.ToString();
        var store = new SqliteStorageInitializer(connectionString);
        var indexer = new RepositoryIndexingService(store);
        await indexer.IndexAsync(repositoryRoot);

        var repositoryId = new RepositoryId(Guid.NewGuid());
        await store.UpsertRepositoryAsync(new Repository(repositoryId, "sample", repositoryRoot));
        var session = await store.CreateSessionAsync(new CreateSessionRequest(repositoryId));

        var engine = new ProtocolEngine(new SearchService(store, store), store, store);
        var response = await engine.ExecuteAsync(new ProtocolRequest("1.0", ProtocolOperations.GetTypeOperation, session.Id.Value.ToString(), JsonSerializer.SerializeToElement(new { query = "Alpha" })));

        Assert.False(response.Success);
        Assert.Contains(response.Errors, error => error.Code == "AmbiguousMatch");
    }

    [Fact]
    public async Task ExecuteAsync_GetCallers_ReturnsCallerMetadataOnly()
    {
        var repositoryRoot = Directory.CreateTempSubdirectory("devx-callers-get").FullName;
        var projectDirectory = Path.Combine(repositoryRoot, "src", "Sample");
        Directory.CreateDirectory(projectDirectory);

        await System.IO.File.WriteAllTextAsync(Path.Combine(projectDirectory, "Sample.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        await System.IO.File.WriteAllTextAsync(Path.Combine(projectDirectory, "Alpha.cs"), """
            namespace Sample;
            public class Alpha
            {
                public void Target() { }
                public void Caller() { Target(); }
            }
            """);

        var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"devx-callers-{Guid.NewGuid():N}.db") }.ToString();
        var store = new SqliteStorageInitializer(connectionString);
        var indexer = new RepositoryIndexingService(store);
        await indexer.IndexAsync(repositoryRoot);

        var repositoryId = new RepositoryId(Guid.NewGuid());
        await store.UpsertRepositoryAsync(new Repository(repositoryId, "sample", repositoryRoot));
        var session = await store.CreateSessionAsync(new CreateSessionRequest(repositoryId));

        var engine = new ProtocolEngine(new SearchService(store, store), store, store);
        var response = await engine.ExecuteAsync(new ProtocolRequest(
            "1.0",
            ProtocolOperations.GetCallers,
            session.Id.Value.ToString(),
            JsonSerializer.SerializeToElement(new { query = "Sample.Alpha.Target" })));

        Assert.True(response.Success);
        var payload = response.Data!.Value.Deserialize<CallersResponse>();
        Assert.NotNull(payload);
        Assert.NotEmpty(payload!.Callers.Callers);
        Assert.DoesNotContain("Target()", response.Data!.Value.GetRawText(), StringComparison.OrdinalIgnoreCase);

        var resumed = await store.GetSessionAsync(new GetSessionRequest(session.Id));
        Assert.NotNull(resumed);
        Assert.Contains(payload.Callers.Callers[0].CallerMethodId, resumed!.WorkingSet);
    }

    [Fact]
    public async Task ExecuteAsync_GetCallees_ReturnsCalleeMetadataOnly()
    {
        var repositoryRoot = Directory.CreateTempSubdirectory("devx-callees-get").FullName;
        var projectDirectory = Path.Combine(repositoryRoot, "src", "Sample");
        Directory.CreateDirectory(projectDirectory);

        await System.IO.File.WriteAllTextAsync(Path.Combine(projectDirectory, "Sample.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        await System.IO.File.WriteAllTextAsync(Path.Combine(projectDirectory, "Alpha.cs"), """
            namespace Sample;
            public class Alpha
            {
                public void Target() { }
                public void Caller() { Target(); }
            }
            """);

        var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"devx-callees-{Guid.NewGuid():N}.db") }.ToString();
        var store = new SqliteStorageInitializer(connectionString);
        var indexer = new RepositoryIndexingService(store);
        await indexer.IndexAsync(repositoryRoot);

        var repositoryId = new RepositoryId(Guid.NewGuid());
        await store.UpsertRepositoryAsync(new Repository(repositoryId, "sample", repositoryRoot));
        var session = await store.CreateSessionAsync(new CreateSessionRequest(repositoryId));

        var engine = new ProtocolEngine(new SearchService(store, store), store, store);
        var response = await engine.ExecuteAsync(new ProtocolRequest(
            "1.0",
            ProtocolOperations.GetCallees,
            session.Id.Value.ToString(),
            JsonSerializer.SerializeToElement(new { query = "Sample.Alpha.Caller" })));

        Assert.True(response.Success);
        var payload = response.Data!.Value.Deserialize<CalleesResponse>();
        Assert.NotNull(payload);
        Assert.NotEmpty(payload!.Callees.Callees);
        Assert.DoesNotContain("Target()", response.Data!.Value.GetRawText(), StringComparison.OrdinalIgnoreCase);

        var resumed = await store.GetSessionAsync(new GetSessionRequest(session.Id));
        Assert.NotNull(resumed);
        Assert.Contains(payload.Callees.Callees[0].CalleeMethodId, resumed!.WorkingSet);
    }

    [Fact]
    public async Task ExecuteAsync_ReadMethod_PrefersExactPythonModulePathOverTestFake()
    {
        var repositoryRoot = Directory.CreateTempSubdirectory("devx-safe-method-read").FullName;
        Directory.CreateDirectory(Path.Combine(repositoryRoot, "tests"));
        await System.IO.File.WriteAllTextAsync(Path.Combine(repositoryRoot, "db.py"), "def set_state_field():\n    return 'real'\n");
        await System.IO.File.WriteAllTextAsync(Path.Combine(repositoryRoot, "tests", "test_unstick.py"), "class _FakeDB:\n    def set_state_field(self):\n        return 'fake'\n");

        var store = new SqliteStorageInitializer(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"devx-safe-read-{Guid.NewGuid():N}.db") }.ToString());
        await new RepositoryIndexingService(store).IndexAsync(repositoryRoot);
        Assert.Equal(2, (await store.FindSymbolsByNameAsync("set_state_field")).Count);
        var repositoryId = new RepositoryId(Guid.NewGuid());
        await store.UpsertRepositoryAsync(new Repository(repositoryId, "sample", repositoryRoot));
        var session = await store.CreateSessionAsync(new CreateSessionRequest(repositoryId));
        var engine = new ProtocolEngine(new SearchService(store, store), store, store);

        var response = await engine.ExecuteAsync(new ProtocolRequest(
            "1.0", "READ_METHOD", session.Id.Value.ToString(),
            JsonSerializer.SerializeToElement(new { query = "db.set_state_field" })));

        Assert.True(response.Success, JsonSerializer.Serialize(response.Errors));
        var payload = response.Data!.Value.Deserialize<SourceReadResponse>();
        Assert.NotNull(payload);
        Assert.Equal("db.py", payload!.Path);
        Assert.Contains("real", payload.Source);
        Assert.DoesNotContain("fake", payload.Source);
    }

    [Fact]
    public async Task ExecuteAsync_ReadMethod_UnqualifiedDuplicateIsAmbiguous()
    {
        var repositoryRoot = Directory.CreateTempSubdirectory("devx-ambiguous-method-read").FullName;
        await System.IO.File.WriteAllTextAsync(Path.Combine(repositoryRoot, "one.py"), "def set_state_field():\n    return 1\n");
        await System.IO.File.WriteAllTextAsync(Path.Combine(repositoryRoot, "two.py"), "def set_state_field():\n    return 2\n");
        var store = new SqliteStorageInitializer(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"devx-ambiguous-read-{Guid.NewGuid():N}.db") }.ToString());
        await new RepositoryIndexingService(store).IndexAsync(repositoryRoot);
        Assert.Equal(2, (await store.FindSymbolsByNameAsync("set_state_field")).Count);
        var repositoryId = new RepositoryId(Guid.NewGuid());
        await store.UpsertRepositoryAsync(new Repository(repositoryId, "sample", repositoryRoot));
        var session = await store.CreateSessionAsync(new CreateSessionRequest(repositoryId));
        var engine = new ProtocolEngine(new SearchService(store, store), store, store);

        var response = await engine.ExecuteAsync(new ProtocolRequest(
            "1.0", "READ_METHOD", session.Id.Value.ToString(),
            JsonSerializer.SerializeToElement(new { query = "set_state_field" })));

        Assert.False(response.Success);
        Assert.Equal("AmbiguousMatch", Assert.Single(response.Errors).Code);
        Assert.Equal(2, response.Errors[0].Suggestions!.Count);
    }

    [Fact]
    public async Task ExecuteAsync_ReadSection_AcceptsUniqueHeadingPrefix()
    {
        var repositoryRoot = Directory.CreateTempSubdirectory("devx-section-prefix").FullName;
        await System.IO.File.WriteAllTextAsync(Path.Combine(repositoryRoot, "CHECKLIST.md"), "# Checklist\n\n## Block 119 — Deploy API\nDo the work.\n\n## Block 120 — Verify API\nCheck it.\n");
        var store = new SqliteStorageInitializer(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Path.GetTempPath(), $"devx-section-{Guid.NewGuid():N}.db") }.ToString());
        await new RepositoryIndexingService(store).IndexAsync(repositoryRoot);
        var repositoryId = new RepositoryId(Guid.NewGuid());
        await store.UpsertRepositoryAsync(new Repository(repositoryId, "sample", repositoryRoot));
        var session = await store.CreateSessionAsync(new CreateSessionRequest(repositoryId));
        var engine = new ProtocolEngine(new SearchService(store, store), store, store);

        var response = await engine.ExecuteAsync(new ProtocolRequest(
            "1.0", "READ_SECTION", session.Id.Value.ToString(),
            JsonSerializer.SerializeToElement(new { file = "CHECKLIST.md", heading = "Block 119" })));

        Assert.True(response.Success);
        var payload = response.Data!.Value.Deserialize<SourceReadResponse>();
        Assert.Contains("Deploy API", payload!.Source);
        Assert.DoesNotContain("Block 120", payload.Source);
    }
}
