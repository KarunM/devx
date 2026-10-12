using System.Text.Json;
using DevX.Domain;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using IOFile = System.IO.File;

namespace DevX.Protocol;

/// <summary>
/// C# `callers` / `callees` for a method with several overloads in one type: each overload is answered
/// on its own. Call sites are read from method bodies and matched to an overload by argument count
/// (the receiver's type is not resolved), so a site that fits several overloads is listed under each.
/// A caller that is itself another overload of the same method is reported as `self-delegation`.
/// </summary>
internal static class CallOverloads
{
    public const string SelfDelegation = "self-delegation";

    private sealed record Arity(int Required, int Max, bool IsExtension);
    private sealed record Site(string Name, int Arguments, bool ViaMember, bool IsCreation, int Line);

    public static string ParameterTypes(string? signature)
    {
        var parameters = ParameterList(signature);
        return parameters is null ? string.Empty : string.Join(", ", parameters.Parameters.Select(p =>
        {
            var modifier = p.Modifiers.Where(m => m.Text is "ref" or "out" or "in").Select(m => m.Text + " ");
            return string.Concat(modifier) + (p.Type?.ToString() ?? p.Identifier.Text);
        }));
    }

    public static string SignatureOf(Symbol symbol) => $"{symbol.QualifiedName}({ParameterTypes(symbol.Signature)})";

    private static ParameterListSyntax? ParameterList(string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature)) return null;
        var tree = CSharpSyntaxTree.ParseText("class _ {\n" + signature + "\n}");
        return tree.GetRoot().DescendantNodes().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault()?.ParameterList;
    }

    private static Arity ArityOf(string? signature)
    {
        var parameters = ParameterList(signature)?.Parameters ?? default;
        var count = parameters.Count;
        var required = parameters.Count(p => p.Default is null && !p.Modifiers.Any(m => m.Text == "params"));
        var max = parameters.Any(p => p.Modifiers.Any(m => m.Text == "params")) ? int.MaxValue : count;
        return new Arity(required, max, count > 0 && parameters[0].Modifiers.Any(m => m.Text == "this"));
    }

    private static bool Fits(Site site, Arity arity)
    {
        var n = site.Arguments + (arity.IsExtension && site.ViaMember ? 1 : 0);
        return n >= arity.Required && n <= arity.Max;
    }

    private static IReadOnlyList<Site> SitesIn(string body)
    {
        var tree = CSharpSyntaxTree.ParseText("class _ {\n" + body + "\n}");
        var sites = new List<Site>();
        foreach (var node in tree.GetRoot().DescendantNodes())
        {
            int Line() => node.GetLocation().GetLineSpan().StartLinePosition.Line;
            switch (node)
            {
                case InvocationExpressionSyntax invocation:
                    {
                        var name = invocation.Expression switch
                        {
                            IdentifierNameSyntax id => id.Identifier.ValueText,
                            GenericNameSyntax generic => generic.Identifier.ValueText,
                            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                            MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
                            _ => null
                        };
                        if (name is not null)
                            sites.Add(new Site(name, invocation.ArgumentList.Arguments.Count, invocation.Expression is MemberAccessExpressionSyntax or MemberBindingExpressionSyntax, false, Line()));
                        break;
                    }
                case ObjectCreationExpressionSyntax creation:
                    {
                        var name = creation.Type switch
                        {
                            IdentifierNameSyntax id => id.Identifier.ValueText,
                            GenericNameSyntax generic => generic.Identifier.ValueText,
                            QualifiedNameSyntax { Right: var right } => right.Identifier.ValueText,
                            _ => null
                        };
                        if (name is not null) sites.Add(new Site(name, creation.ArgumentList?.Arguments.Count ?? 0, false, true, Line()));
                        break;
                    }
            }
        }
        return sites;
    }

    private sealed class Sources
    {
        private readonly Dictionary<string, string[]?> _cache = new(StringComparer.Ordinal);

        public IReadOnlyList<Site> SitesOf(MethodCandidate candidate)
        {
            var symbol = candidate.Symbol;
            if (symbol.StartLine is not { } start || symbol.EndLine is not { } end || start < 1 || end < start) return [];
            var full = Path.Combine(candidate.RootPath, candidate.Symbol.FilePath!.Replace('/', Path.DirectorySeparatorChar));
            if (!_cache.TryGetValue(full, out var lines))
            {
                lines = IOFile.Exists(full) ? IOFile.ReadAllText(full).Replace("\r\n", "\n").Split('\n') : null;
                _cache[full] = lines;
            }
            if (lines is null || end > lines.Length) return [];
            // Wrapper line 1 holds the first body line, so a site's file line is start + (line - 1).
            return SitesIn(string.Join('\n', lines[(start - 1)..end]))
                .Select(site => site with { Line = start + site.Line - 1 }).ToList();
        }
    }

    public static IReadOnlyList<CallerOverload> Callers(IReadOnlyList<Symbol> overloads, IReadOnlyList<MethodCandidate> candidates)
    {
        var arities = overloads.Select(o => ArityOf(o.Signature)).ToList();
        var name = overloads[0].Name;
        var wantsCreation = overloads[0].Kind == "Constructor";
        var found = overloads.Select(_ => new Dictionary<string, CallerSummary>(StringComparer.Ordinal)).ToList();
        var sources = new Sources();
        foreach (var candidate in candidates)
        {
            foreach (var site in sources.SitesOf(candidate).Where(s => s.Name == name && s.IsCreation == wantsCreation))
            {
                var fitting = Enumerable.Range(0, overloads.Count).Where(i => Fits(site, arities[i])).ToList();
                if (fitting.Count == 0) fitting = Enumerable.Range(0, overloads.Count).ToList();
                foreach (var i in fitting)
                {
                    if (candidate.Symbol.Id == overloads[i].Id || found[i].ContainsKey(candidate.Symbol.Id)) continue;
                    var delegating = candidate.Symbol.QualifiedName == overloads[i].QualifiedName;
                    found[i][candidate.Symbol.Id] = new CallerSummary(
                        candidate.Symbol.Id, candidate.Symbol.Name, candidate.Symbol.QualifiedName, candidate.Namespace, candidate.Project,
                        candidate.Symbol.FilePath!, candidate.TypeName, site.Line,
                        delegating ? SelfDelegation : null, delegating ? SignatureOf(candidate.Symbol) : null);
                }
            }
        }
        return overloads.Select((o, i) => new CallerOverload(
            SignatureOf(o), o.FilePath ?? string.Empty, o.StartLine ?? 0, "arity",
            found[i].Values
                .OrderBy(c => c.Relationship is null ? 0 : 1)
                .ThenBy(c => c.QualifiedName, StringComparer.Ordinal).ThenBy(c => c.FilePath, StringComparer.Ordinal).ThenBy(c => c.CallSiteLine)
                .ToList())).ToList();
    }

    public static IReadOnlyList<CalleeOverload> Callees(IReadOnlyList<Symbol> overloads, IReadOnlyList<MethodCandidate> candidates)
    {
        var sources = new Sources();
        var arityById = new Dictionary<string, Arity>(StringComparer.Ordinal);
        Arity ArityFor(MethodCandidate c) => arityById.TryGetValue(c.Symbol.Id, out var a) ? a : arityById[c.Symbol.Id] = ArityOf(c.Symbol.Signature);
        var result = new List<CalleeOverload>();
        foreach (var overload in overloads)
        {
            var own = candidates.FirstOrDefault(c => c.Symbol.Id == overload.Id);
            var callees = new Dictionary<string, CalleeSummary>(StringComparer.Ordinal);
            if (own is not null)
            {
                foreach (var site in sources.SitesOf(own))
                {
                    var named = candidates.Where(c => c.Symbol.Name == site.Name && c.Symbol.Id != overload.Id && (c.Symbol.Kind == "Constructor") == site.IsCreation).ToList();
                    var fitting = named.Where(c => Fits(site, ArityFor(c))).ToList();
                    foreach (var callee in fitting.Count > 0 ? fitting : named)
                    {
                        if (callees.ContainsKey(callee.Symbol.Id)) continue;
                        var delegating = callee.Symbol.QualifiedName == overload.QualifiedName;
                        callees[callee.Symbol.Id] = new CalleeSummary(
                            callee.Symbol.Id, callee.Symbol.Name, callee.Symbol.QualifiedName, callee.Namespace, callee.Project,
                            callee.Symbol.FilePath!, callee.TypeName, null,
                            delegating ? SelfDelegation : null, delegating ? SignatureOf(callee.Symbol) : null);
                    }
                }
            }
            result.Add(new CalleeOverload(SignatureOf(overload), overload.FilePath ?? string.Empty, overload.StartLine ?? 0,
                callees.Values.OrderBy(c => c.Relationship is null ? 0 : 1).ThenBy(c => c.QualifiedName, StringComparer.Ordinal).ToList()));
        }
        return result;
    }

    /// <summary>Agent rendering of an overload-grouped answer: a header per overload, its callers/callees indented under it.</summary>
    public static string? Format(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        foreach (var (root, items, signatureKey) in new[] { ("callers", "callers", "callerSignature"), ("callees", "callees", "calleeSignature") })
        {
            if (!Prop(data, root, out var body) || body.ValueKind != JsonValueKind.Object) continue;
            if (!Prop(body, "overloads", out var overloads) || overloads.ValueKind != JsonValueKind.Array) continue;
            var lines = new List<string>();
            foreach (var overload in overloads.EnumerateArray())
            {
                lines.Add($"{Str(overload, "signature")} {Str(overload, "path")}:{Str(overload, "startLine")}");
                var entries = Prop(overload, items, out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().ToList() : [];
                if (entries.Count == 0) lines.Add("  (none)");
                foreach (var entry in entries)
                {
                    var relationship = Str(entry, "relationship");
                    var name = relationship.Length > 0 ? Str(entry, signatureKey) : Str(entry, "qualifiedName");
                    lines.Add($"  {Str(entry, "filePath")}:{Str(entry, "callSiteLine")}: {name}" + (relationship.Length > 0 ? $"  [{relationship}]" : string.Empty));
                }
            }
            return string.Join("\n", lines);
        }
        return null;
    }

    private static bool Prop(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default;
        return false;
    }

    private static string Str(JsonElement element, string name) => Prop(element, name, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : string.Empty;
}
