using Ecp.Indexer;
using Xunit;

namespace Ecp.Tests.Indexer;

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
}
