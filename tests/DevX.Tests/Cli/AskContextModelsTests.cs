using System.Text.Json;
using DevX.Cli;
using Xunit;

namespace DevX.Tests.Cli;

public class AskContextModelsTests
{
    [Fact]
    public void Parse_ContextRequestJson_MapsAllRequestTypes()
    {
        var json = """
        {
          "kind": "context_request",
          "reason": "Need the actual CLI entry point and command wiring.",
          "requests": [
            { "type": "search_symbol", "query": "RunAsync" },
            { "type": "search_text", "query": "health command" },
            { "type": "read_file", "path": "src/DevX.Cli/Program.cs", "range": "1:200" },
            { "type": "read_method", "id": "DevX.Cli.Program.RunAsync" },
            { "type": "read_type", "id": "DevX.Cli.AskOrchestrator" }
          ]
        }
        """;

        var result = AskContextRequestParser.Parse(json);

        Assert.Equal("Need the actual CLI entry point and command wiring.", result.Reason);
        Assert.Collection(result.Requests,
            request =>
            {
                Assert.Equal("search_symbol", request.Type);
                Assert.Equal("RunAsync", request.Query);
                Assert.Null(request.Path);
                Assert.Null(request.Range);
                Assert.Null(request.Id);
            },
            request =>
            {
                Assert.Equal("search_text", request.Type);
                Assert.Equal("health command", request.Query);
            },
            request =>
            {
                Assert.Equal("read_file", request.Type);
                Assert.Equal("src/DevX.Cli/Program.cs", request.Path);
                Assert.Equal("1:200", request.Range);
            },
            request =>
            {
                Assert.Equal("read_method", request.Type);
                Assert.Equal("DevX.Cli.Program.RunAsync", request.Id);
            },
            request =>
            {
                Assert.Equal("read_type", request.Type);
                Assert.Equal("DevX.Cli.AskOrchestrator", request.Id);
            });
    }
}