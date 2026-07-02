using Ecp.Domain;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ecp.Indexer;

public sealed class CSharpRoslynParser : ILanguageParser
{
    public bool CanParse(string filePath) => Path.GetExtension(filePath).Equals(".cs", StringComparison.OrdinalIgnoreCase);

    public ParsedFile Parse(string filePath, string sourceText)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceText);
        var root = tree.GetCompilationUnitRoot();
        var ns = root.Members.OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();

        var types = root.DescendantNodes()
            .Where(n => n is TypeDeclarationSyntax)
            .Cast<TypeDeclarationSyntax>()
            .Select(type => new ParsedType(
                type.Identifier.ValueText,
                type.Kind().ToString().Replace("Declaration", "", StringComparison.OrdinalIgnoreCase),
                ns,
                GetVisibility(type.Modifiers),
                GetLine(type, true),
                GetLine(type, false),
                type.Members.OfType<MethodDeclarationSyntax>()
                    .Select(m => new ParsedMethod(m.Identifier.ValueText, m.ToString(), m.ReturnType.ToString(), GetVisibility(m.Modifiers), GetLine(m, true), GetLine(m, false), false))
                    .Concat(type.Members.OfType<ConstructorDeclarationSyntax>()
                        .Select(c => new ParsedMethod(c.Identifier.ValueText, c.ToString(), string.Empty, GetVisibility(c.Modifiers), GetLine(c, true), GetLine(c, false), true)))
                    .ToList(),
                type.Members.OfType<PropertyDeclarationSyntax>()
                    .Select(p => new ParsedProperty(p.Identifier.ValueText, p.Type.ToString(), GetVisibility(p.Modifiers), GetLine(p, true), GetLine(p, false)))
                    .ToList(),
                type.Members.OfType<FieldDeclarationSyntax>()
                    .SelectMany(f => f.Declaration.Variables.Select(v => new ParsedField(v.Identifier.ValueText, f.Declaration.Type.ToString(), GetVisibility(f.Modifiers), GetLine(f, true), GetLine(f, false))))
                    .ToList()))
            .ToList();

        return new ParsedFile(filePath, ns, types);
    }

    private static string GetVisibility(SyntaxTokenList modifiers)
    {
        if (modifiers.Any(SyntaxKind.PublicKeyword)) return "public";
        if (modifiers.Any(SyntaxKind.PrivateKeyword)) return "private";
        if (modifiers.Any(SyntaxKind.ProtectedKeyword)) return "protected";
        if (modifiers.Any(SyntaxKind.InternalKeyword)) return "internal";
        return "private";
    }

    private static int GetLine(SyntaxNode node, bool start)
    {
        var span = start ? node.GetLocation().GetLineSpan().StartLinePosition : node.GetLocation().GetLineSpan().EndLinePosition;
        return span.Line + 1;
    }
}
