using DevX.Domain;
using Xunit;

namespace DevX.Tests.Indexer;

/// <summary>
/// Covers module-level Python assignments (VOICE_ID = "..." style constants) surfacing as Fields on
/// the synthetic "&lt;module&gt;" type, so `devx search symbol` can find them the same way it finds
/// classes, functions and methods. Shells out to the real parse_symbols.py helper (via
/// PythonSymbolReader.Parse), matching how PythonAstParser and RepositoryIndexingService use it.
/// </summary>
public sealed class PythonSymbolReaderTests
{
    [Fact]
    public void ResolveScriptPath_EmptyAssemblyLocationFallsBackToBaseDirectory()
    {
        var result = PythonSymbolReader.ResolveScriptPath(
            string.Empty,
            "/app",
            null,
            _ => false);

        Assert.Equal(Path.Combine("/app", "PythonAst", "parse_symbols.py"), result);
    }

    [Fact]
    public void ResolveScriptPath_PrefersAssemblyDirectoryWhenScriptExistsThere()
    {
        var assemblyCandidate = Path.Combine("/assembly", "PythonAst", "parse_symbols.py");

        var result = PythonSymbolReader.ResolveScriptPath(
            "/assembly/DevX.Domain.dll",
            "/app",
            "/process/devx",
            path => path == assemblyCandidate);

        Assert.Equal(assemblyCandidate, result);
    }

    private static ParsedFile ParseSource(string source)
    {
        var path = Path.Combine(Path.GetTempPath(), $"devx-py-{Guid.NewGuid():N}.py");
        System.IO.File.WriteAllText(path, source);
        try
        {
            return PythonSymbolReader.Parse(path);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void Parse_ModuleLevelAssignmentsBecomeFieldsOnModuleType()
    {
        var parsed = ParseSource("""
            VOICE_ID = "abc123"
            VOICE_DIR = "/voices"


            def helper():
                pass
            """);

        var module = Assert.Single(parsed.Types, type => type.Name == "<module>");
        Assert.Equal(["VOICE_ID", "VOICE_DIR"], module.Fields.Select(field => field.Name));
        Assert.Equal(["helper"], module.Methods.Select(method => method.Name));
    }

    [Fact]
    public void Parse_ModuleLevelAssignmentWithNoFunctionsStillEmitsModuleType()
    {
        var parsed = ParseSource("""
            RELATIONSHIP_STAGE_TEXT = {
                1: "stranger",
                2: "friend",
            }
            """);

        var module = Assert.Single(parsed.Types, type => type.Name == "<module>");
        Assert.Equal("RELATIONSHIP_STAGE_TEXT", Assert.Single(module.Fields).Name);
        Assert.Empty(module.Methods);
    }

    [Fact]
    public void Parse_ModuleLevelFieldHasCorrectStartAndEndLine()
    {
        var parsed = ParseSource("""
            import os

            VOICE_DIR = os.path.join(
                "assets",
                "voices",
            )
            """);

        var module = Assert.Single(parsed.Types, type => type.Name == "<module>");
        var field = Assert.Single(module.Fields);
        Assert.Equal("VOICE_DIR", field.Name);
        Assert.Equal(3, field.StartLine);
        Assert.Equal(6, field.EndLine);
    }

    [Fact]
    public void Parse_ModuleLevelAnnotatedAssignmentBecomesFieldWithType()
    {
        var parsed = ParseSource("""
            MAX_RETRIES: int = 3
            """);

        var module = Assert.Single(parsed.Types, type => type.Name == "<module>");
        var field = Assert.Single(module.Fields);
        Assert.Equal("MAX_RETRIES", field.Name);
        Assert.Equal("int", field.Type);
    }

    [Fact]
    public void Parse_ClassFieldsAndModuleFieldsBothPresentAndDoNotCollide()
    {
        var parsed = ParseSource("""
            MODULE_CONST = 1


            class Thing:
                class_field = 2
            """);

        var module = Assert.Single(parsed.Types, type => type.Name == "<module>");
        Assert.Equal("MODULE_CONST", Assert.Single(module.Fields).Name);

        var thing = Assert.Single(parsed.Types, type => type.Name == "Thing");
        Assert.Equal("class_field", Assert.Single(thing.Fields).Name);
    }

    [Fact]
    public void Parse_NoModuleLevelAssignmentsOrFunctionsProducesNoModuleType()
    {
        var parsed = ParseSource("""
            class Thing:
                pass
            """);

        Assert.DoesNotContain(parsed.Types, type => type.Name == "<module>");
    }
}
