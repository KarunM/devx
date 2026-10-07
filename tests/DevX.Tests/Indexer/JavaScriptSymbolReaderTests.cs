using DevX.Domain;
using DevX.Indexer;
using Xunit;

namespace DevX.Tests.Indexer;

public sealed class JavaScriptSymbolReaderTests
{
    private const string Sample = """
        const RULES = [
          { id: 1, text: "}" },
        ];
        const CONFIG = {
          a: 1,
        };
        // function commented() {}
        function traverse(node, fn) {
          const s = "function fake() {";
          if (node) { fn(node); }
        }
        const buildTemplate = (a, b) => {
          return `{${a}}`;
        };
        const short = x => x + 1;
        let assess = function (p) {
          return /[}]/.test(p);
        };
        const notAFunction = 5;
        class Engine extends Base {
          constructor(x) {
            super(x);
          }
          static async run(a) {
            if (a) { return 1; }
          }
          get size() { return 2; }
        }
        """;

    private static ParsedFile Parse() => JavaScriptSymbolReader.Parse("js/engine.js", Sample);

    [Theory]
    [InlineData("a.js", true)]
    [InlineData("a.MJS", true)]
    [InlineData("a.py", false)]
    [InlineData("a.json", false)]
    public void CanParse_ByExtension(string path, bool expected) => Assert.Equal(expected, JavaScriptSymbolReader.CanParse(path));

    [Fact]
    public void Parse_FindsFunctionsArrowsAndFunctionExpressions()
    {
        var module = Parse().Types.Single(t => t.Kind == "Module");
        Assert.Equal(["traverse", "buildTemplate", "short", "assess"], module.Methods.Select(m => m.Name));
    }

    [Fact]
    public void Parse_MethodsCarryBodySpans()
    {
        var module = Parse().Types.Single(t => t.Kind == "Module");
        var traverse = module.Methods.Single(m => m.Name == "traverse");
        Assert.Equal((8, 11), (traverse.StartLine, traverse.EndLine));
        var build = module.Methods.Single(m => m.Name == "buildTemplate");
        Assert.Equal((12, 14), (build.StartLine, build.EndLine));
        var assess = module.Methods.Single(m => m.Name == "assess");
        Assert.Equal((16, 18), (assess.StartLine, assess.EndLine));
    }

    [Fact]
    public void Parse_TopLevelCollectionConstsBecomeFields()
    {
        var module = Parse().Types.Single(t => t.Kind == "Module");
        Assert.Equal(["RULES", "CONFIG"], module.Fields.Select(f => f.Name));
        Assert.Equal((1, 3), (module.Fields[0].StartLine, module.Fields[0].EndLine));
    }

    [Fact]
    public void Parse_ClassHasMethodsWithSpans()
    {
        var cls = Parse().Types.Single(t => t.Name == "Engine");
        Assert.Equal(["constructor", "run", "size"], cls.Methods.Select(m => m.Name));
        Assert.True(cls.Methods[0].IsConstructor);
        var run = cls.Methods.Single(m => m.Name == "run");
        Assert.Equal((24, 26), (run.StartLine, run.EndLine));
    }

    [Fact]
    public void CompositeParser_RoutesJavaScript()
    {
        var parsed = new CompositeLanguageParser().Parse("js/engine.js", Sample);
        Assert.Contains(parsed.Types, t => t.Name == "Engine");
    }

    [Theory]
    [InlineData("(function () {\n  function inner(a) {\n    return a;\n  }\n})();")]
    [InlineData(";(function (root) {\n  function inner(a) {\n    return a;\n  }\n})(this);")]
    [InlineData("(function(){\n  function inner(a) {\n    return a;\n  }\n}());")]
    [InlineData("(() => {\n  function inner(a) {\n    return a;\n  }\n})();")]
    [InlineData("(async () => {\n  function inner(a) {\n    return a;\n  }\n})();")]
    [InlineData("!function(){\n  function inner(a) {\n    return a;\n  }\n}();")]
    public void Parse_IifeBodyDeclarationsAreModuleLevel(string source)
    {
        var module = JavaScriptSymbolReader.Parse("js/x.js", source).Types.Single(t => t.Kind == "Module");
        var inner = Assert.Single(module.Methods);
        Assert.Equal("inner", inner.Name);
        Assert.Equal((2, 4), (inner.StartLine, inner.EndLine));
    }

    [Fact]
    public void Parse_IifeExposesArrowConstsClassesAndFields()
    {
        var parsed = JavaScriptSymbolReader.Parse("js/x.js", "(function () {\n  const f = (a) => a;\n  const DATA = [\n    1,\n  ];\n  class K {\n    m() { return 1; }\n  }\n})();\nfunction outer() {\n}\n");
        var module = parsed.Types.Single(t => t.Kind == "Module");
        Assert.Equal(["f", "outer"], module.Methods.Select(m => m.Name));
        Assert.Equal(["DATA"], module.Fields.Select(f => f.Name));
        Assert.Equal(["m"], parsed.Types.Single(t => t.Name == "K").Methods.Select(m => m.Name));
    }
}
