using DevX.Indexer;
using Xunit;

namespace DevX.Tests.Indexer;

public sealed class CSharpRoslynParserTests
{
    [Fact]
    public void Parse_ExtractsNamespacesTypesAndMembers()
    {
        var parser = new CSharpRoslynParser();
        var result = parser.Parse("Sample.cs", """
            namespace Sample;

            public class Demo
            {
                public Demo() { }

                public string Name { get; set; }

                private int _count;

                public void Run() { }
            }
            """);

        Assert.Equal("Sample", result.Namespace);
        Assert.Single(result.Types);

        var type = result.Types[0];
        Assert.Equal("Demo", type.Name);
        Assert.Equal("Class", type.Kind);
        Assert.Contains(type.Methods, m => m.IsConstructor);
        Assert.Contains(type.Methods, m => m.Name == "Run");
        Assert.Contains(type.Properties, p => p.Name == "Name");
        Assert.Contains(type.Fields, f => f.Name == "_count");
    }

    [Fact]
    public void Parse_IndexesTopLevelLocalFunctionsAndLongBodies()
    {
        var body = string.Join("\n", Enumerable.Range(0, 350).Select(index => $"    var value{index} = {index};"));
        var result = new CSharpRoslynParser().Parse("Program.cs", "static void HealthAsync()\n{\n" + body + "\n}");

        var program = Assert.Single(result.Types, type => type.Name == "Program");
        var method = Assert.Single(program.Methods, item => item.Name == "HealthAsync");
        Assert.Equal(1, method.StartLine);
        Assert.Equal(353, method.EndLine);
        Assert.Equal(353, method.EndLine - method.StartLine + 1);
        Assert.Contains("value349", method.Signature);
    }
}
