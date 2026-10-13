using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using DevX.Application;
using DevX.Domain;
using DevX.Indexer;
using IOFile = System.IO.File;

namespace DevX.Protocol;

public sealed partial class ProtocolEngine
{
    private async Task<ProtocolResponse> HandleReadFileAsync(ProtocolRequest request, SessionState session, CancellationToken cancellationToken)
    {
        if (!TryGetQuery(request.Payload, out var path))
        {
            return Fail("ValidationError", "Read file requests must include a string 'query' field.");
        }

        var full = TryGetBoolean(request.Payload, "full");
        var startLine = 0;
        var endLine = 0;
        if (!full && !TryGetRange(request.Payload, out startLine, out endLine))
        {
            return Fail("ValidationError", "Read file requests must include either a 'range' field formatted as 'start:end' or 'start-end', or --full.");
        }

        var root = await _repositoryStore.GetRepositoryRootPathAsync(session.RepositoryId, cancellationToken);
        if (string.IsNullOrWhiteSpace(root))
        {
            return Fail("RepositoryNotIndexed", "Repository root was not found.");
        }

        if (!RepositoryPathGuard.TryResolve(root, path, out var absolutePath, out var pathError))
        {
            return Fail("ValidationError", pathError);
        }

        if (!IOFile.Exists(absolutePath)) return Fail("FileMissing", $"file missing: {path}", path);
        var source = await IOFile.ReadAllTextAsync(absolutePath, cancellationToken);
        var lines = source.Split('\n');
        var safeStart = full ? 1 : Math.Max(1, startLine);
        var requestedEnd = full ? lines.Length : Math.Min(endLine, lines.Length);
        // A range is capped and paged, never silently widened: an agent that asks for 1-99999
        // gets a slice plus the range that continues it, rather than a whole file in its context.
        var safeEnd = full ? requestedEnd : Math.Min(requestedEnd, safeStart + MaxRangeLines - 1);
        if (safeStart > safeEnd)
        {
            return Fail("ValidationError", "Invalid line range.");
        }

        var slice = string.Join('\n', lines[(safeStart - 1)..safeEnd]);
        var nextRange = safeEnd < requestedEnd ? $"{safeEnd + 1}:{requestedEnd}" : null;
        var response = new SourceReadResponse(
            Kind: "file",
            SymbolId: path,
            QualifiedName: path,
            Path: path,
            StartLine: safeStart,
            EndLine: safeEnd,
            TotalLines: lines.Length,
            ReturnedLines: safeEnd - safeStart + 1,
            Truncated: safeEnd < lines.Length,
            SourceReturned: true,
            Source: slice,
            Metadata: new { Language = Path.GetExtension(path).TrimStart('.').ToUpperInvariant(), NextRange = nextRange });
        return new ProtocolResponse(true, JsonSerializer.SerializeToElement(response), [], []);
    }

    private const int MaxRangeLines = 400;

    private async Task<ProtocolResponse> HandleReadMethodAsync(ProtocolRequest request, SessionState session, CancellationToken cancellationToken)
    {
        var query = GetReadQuery(request.Payload);
        if (query is null)
        {
            return Fail("ValidationError", "Read method requests must include either a string 'query' field or an 'id' field.");
        }

        if (_repositoryStore is ISymbolStore)
        {
            var root = await _repositoryStore.GetRepositoryRootPathAsync(session.RepositoryId, cancellationToken);
            if (root is not null)
            {
                var resolution = await ReadableSymbolResolver.ResolveAsync(_repositoryStore, root, query, cancellationToken);
                if (resolution.Candidates.Count > 1)
                {
                    var candidates = RankCandidates(resolution.Candidates, query);
                    var total = resolution.Candidates.Count;
                    return new ProtocolResponse(false, null, [], [new ProtocolError("AmbiguousMatch",
                        $"Symbol '{query}' matched multiple definitions ({total}); pass a qualified name.",
                        query, candidates.Select(s => s.QualifiedName).ToList(), candidates, total)]);
                }
                if (resolution.Candidates.Count == 1)
                {
                    var symbol = resolution.Candidates[0];
                    if (!RepositoryPathGuard.TryResolve(root, symbol.FilePath!, out var absolute, out var pathError))
                        return Fail("ValidationError", pathError, query);
                    if (!IOFile.Exists(absolute)) return Fail("FileMissing", $"file missing: {symbol.FilePath}", query);
                    if (symbol.StartLine is not null && symbol.EndLine is not null)
                    {
                        var lines = (await IOFile.ReadAllTextAsync(absolute, cancellationToken)).Replace("\r\n", "\n").Split('\n');
                        if (symbol.StartLine < 1 || symbol.EndLine > lines.Length) return Fail("StaleIndex", $"Indexed span is stale for {symbol.FilePath}; run devx index update.", query);
                        var source = string.Join('\n', lines[(symbol.StartLine.Value - 1)..symbol.EndLine.Value]);
                        var symbolId = symbol.Id;
                        // Preserve established method source/identity contracts after the new resolver
                        // has constrained the target. Never accept source from a different workspace.
                        if (symbol.Kind is "Method" or "Constructor")
                        {
                            var legacy = (await _repositoryStore.FindMethodSourcesAsync(symbol.QualifiedName, cancellationToken))
                                .Where(method => method.FilePath == symbol.FilePath && method.QualifiedName == symbol.QualifiedName)
                                .DistinctBy(method => method.Id).ToList();
                            if (legacy.Count == 1 && string.Join('\n', lines).Contains(legacy[0].Source.Replace("\r\n", "\n"), StringComparison.Ordinal))
                            {
                                symbolId = legacy[0].Id;
                                source = legacy[0].Source.Replace("\r\n", "\n");
                            }
                        }
                        var kind = Path.GetExtension(symbol.FilePath!).Equals(".md", StringComparison.OrdinalIgnoreCase) ? "section" : symbol.Kind is "Method" or "Constructor" ? "method" : symbol.Kind.ToLowerInvariant();
                        var message = symbol.Kind is "Method" or "Constructor" ? null : $"{query} is a {symbol.Kind}; returned its definition";
                        var symbolReturnedLines = source.Split('\n').Length;
                        var symbolTotalLines = Math.Max(lines.Length - (lines[^1].Length == 0 ? 1 : 0), symbolReturnedLines);
                        object symbolMetadata;
                        if ((kind is "method" or "function") && WantsReadContext(request.Payload))
                        {
                            var indexedSymbol = _repositoryStore is ISymbolStore symbolStore
                                ? (await symbolStore.FindSymbolsByQualifiedNameAsync(symbol.QualifiedName, cancellationToken)).FirstOrDefault(candidate => candidate.Id == symbol.Id)
                                : null;
                            var context = indexedSymbol is null
                                ? new { Complete = false, Basis = "bounded indexed path/name candidates", TestNameMatches = Array.Empty<object>(), SameFileCandidates = Array.Empty<object>(), TestNameMatchesTruncated = false, SameFileCandidatesTruncated = false, SourceNamesTruncated = false, Note = "The resolved symbol has no indexed symbol record for context lookup." }
                                : await BuildReadContextAsync(indexedSymbol, source, cancellationToken);
                            symbolMetadata = new { ResolvedVia = resolution.ResolvedVia, SymbolKind = symbol.Kind, Message = message, Context = context };
                        }
                        else
                        {
                            symbolMetadata = new { ResolvedVia = resolution.ResolvedVia, SymbolKind = kind == "section" ? "Section" : symbol.Kind, Message = message };
                        }
                        var result = new SourceReadResponse(kind, symbolId, symbol.QualifiedName, symbol.FilePath!, symbol.StartLine.Value,
                            symbol.EndLine.Value, symbolTotalLines, symbolReturnedLines, symbolReturnedLines < symbol.EndLine.Value - symbol.StartLine.Value + 1, true, source, symbolMetadata);
                        await RecordHistoryAsync(() => _sessionStore.MarkSymbolExpandedAsync(new MarkSymbolExpandedRequest(session.Id, symbolId), cancellationToken));
                        return new ProtocolResponse(true, JsonSerializer.SerializeToElement(result), [], []);
                    }
                }
                // Legacy source IDs use a different identity from indexed symbols; retain ID support.
                if (resolution.Candidates.Count == 0 && !Guid.TryParse(query, out _))
                {
                    // A dotted miss still has to hand back names the caller can paste. LG1 was
                    // told only "not in this workspace" and left the tool for grep; the index
                    // can always answer for the last segment, which is one prefix shorter.
                    // The workspace fact is still worth saying -- it is how a caller knows the
                    // name belongs to a different checkout rather than being misspelled -- so
                    // it is kept and the pasteable names are added to it.
                    return await NotFoundWithSuggestionsAsync(
                        "Symbol",
                        query,
                        cancellationToken,
                        $"Symbol '{query}' is not in this workspace: {root}.");
                }
            }
        }
        var methods = await ResolveMethodSourcesAsync(query, request.Payload, cancellationToken);
        methods = ResolveSafeMethodMatches(methods, query);
        if (methods.Count == 0)
        {
            return await NotFoundWithSuggestionsAsync("Method", query, cancellationToken);
        }

        if (methods.Count > 1)
        {
            return Fail("AmbiguousMatch", $"Method '{query}' matched multiple implementations.", query, methods.Select(method => method.QualifiedName));
        }

        var method = methods[0];
        var methodRoot = await _repositoryStore.GetRepositoryRootPathAsync(session.RepositoryId, cancellationToken);
        if (string.IsNullOrWhiteSpace(methodRoot))
        {
            return Fail("RepositoryNotIndexed", "Repository root was not found.");
        }
        if (!RepositoryPathGuard.TryResolve(methodRoot, method.FilePath, out _, out var methodPathError))
        {
            return Fail("ValidationError", methodPathError, query);
        }
        var slice = method.Source.Replace("\r\n", "\n");
        var returnedLines = slice.Split('\n').Length;
        var snapshot = await _repositoryStore.GetFileSnapshotAsync(method.FilePath, cancellationToken);
        var totalLines = Math.Max(snapshot?.ApproximateLoc ?? method.EndLine, returnedLines);
        object metadata;
        if (WantsReadContext(request.Payload))
        {
            var indexedSymbol = _repositoryStore is IReadContextStore contextStore
                ? await contextStore.FindReadContextTargetAsync(session.RepositoryId, method.FilePath, method.QualifiedName, cancellationToken)
                : null;
            var context = indexedSymbol is null
                ? new { Complete = false, Basis = "bounded indexed path/name candidates", TestNameMatches = Array.Empty<object>(), SameFileCandidates = Array.Empty<object>(), TestNameMatchesTruncated = false, SameFileCandidatesTruncated = false, SourceNamesTruncated = false, Note = _repositoryStore is IReadContextStore ? "The resolved method has no indexed symbol record for context lookup." : "This store does not provide read context candidates." }
                : await BuildReadContextAsync(indexedSymbol, slice, cancellationToken);
            metadata = new { ResolvedVia = Guid.TryParse(query, out _) ? "exact" : "short-name", Context = context };
        }
        else
        {
            metadata = new { ResolvedVia = Guid.TryParse(query, out _) ? "exact" : "short-name" };
        }
        var response = new SourceReadResponse("method", method.Id, method.QualifiedName, method.FilePath, method.StartLine, method.EndLine, totalLines, returnedLines, returnedLines < method.EndLine - method.StartLine + 1, true, slice, metadata);
        await RecordHistoryAsync(() => _sessionStore.MarkSymbolExpandedAsync(new MarkSymbolExpandedRequest(session.Id, method.Id), cancellationToken));
        return new ProtocolResponse(true, JsonSerializer.SerializeToElement(response), [], []);
    }

    private static bool WantsReadContext(JsonElement payload)
        => payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("context", out var context)
            && context.ValueKind == JsonValueKind.True;

    private async Task<object> BuildReadContextAsync(Symbol target, string source, CancellationToken cancellationToken)
    {
        const int sourceScanLimit = 32 * 1024;
        var sourceScan = source.Length > sourceScanLimit ? source[..sourceScanLimit] : source;
        var sourceNamesTruncated = source.Length > sourceScanLimit;
        var sourceNames = Regex.Matches(sourceScan, @"([A-Za-z_]\w*)\s*\(")
            .Select(match => match.Groups[1].Value)
            .Where(name => name is not ("if" or "for" or "while" or "switch" or "catch" or "def" or "with" or "return" or "assert" or "lambda"))
            .Distinct(StringComparer.Ordinal)
            .Take(65)
            .ToList();
        if (_repositoryStore is not IReadContextStore contextStore)
        {
            return new
            {
                Complete = false,
                Basis = "bounded indexed path/name candidates",
                TestNameMatches = Array.Empty<object>(),
                SameFileCandidates = Array.Empty<object>(),
                TestNameMatchesTruncated = false,
                SameFileCandidatesTruncated = false,
                SourceNamesTruncated = sourceNamesTruncated || sourceNames.Count > 64,
                Note = "This store does not provide read context candidates."
            };
        }

        var candidates = await contextStore.FindReadContextCandidatesAsync(target, sourceNames, sourceNamesTruncated, 5, cancellationToken);
        return new
        {
            Complete = false,
            Basis = "bounded indexed path/name candidates",
            TestNameMatches = candidates.TestNameMatches.Select(symbol => ContextCandidate(symbol, "test-name-match", "exact test name and same-module test path")).ToList(),
            SameFileCandidates = candidates.SameFileDefinitions.Select(symbol => ContextCandidate(
                symbol,
                candidates.SameFileDefinitionsAreNameMatches ? "source-name-match" : "same-file-definition",
                candidates.SameFileDefinitionsAreNameMatches ? "lexical source name and indexed definition" : "indexed definition in the same file")).ToList(),
            TestNameMatchesTruncated = candidates.TestNameMatchesTruncated,
            SameFileCandidatesTruncated = candidates.SameFileDefinitionsTruncated,
            SourceNamesTruncated = candidates.SourceNamesTruncated,
            Note = "Candidates are navigation hints; name and path matches do not establish call or test relations."
        };
    }

    private static object ContextCandidate(Symbol symbol, string relation, string evidence)
        => new
        {
            SymbolId = symbol.Id,
            symbol.Name,
            symbol.QualifiedName,
            symbol.Kind,
            symbol.FilePath,
            symbol.StartLine,
            symbol.EndLine,
            Relation = relation,
            Evidence = evidence
        };

    // Member lines omit the path (always the header's file) to keep the outline small.
    // Stored signatures may span lines (C# keeps the body braces); the outline shows the first line only.
    private static string OutlineSignature(string? signature)
    {
        var first = (signature ?? "").Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0) ?? "";
        if (first.Length > 160) first = first[..160];
        return first.Length == 0 ? "" : " " + first;
    }

    private async Task<(string Text, int Count, IReadOnlyList<TypeBase> Bases)> BuildTypeOutlineAsync(string qualifiedName, string filePath, int startLine, int endLine, string[] fileLines, string? root, CancellationToken cancellationToken)
    {
        // Signature: the declaration line, extended while parentheses are open (multi-line Python bases)
        // or until a line ends with ':' (Python). C#-style declarations stop after the first line.
        var signature = new List<string>();
        var depth = 0;
        for (var i = startLine - 1; i < endLine && i < fileLines.Length; i++)
        {
            var line = fileLines[i];
            signature.Add(line);
            depth += line.Count(c => c is '(' or '[') - line.Count(c => c is ')' or ']');
            if (depth <= 0) break;
        }
        var members = _repositoryStore is ISymbolStore symbols
            ? (await symbols.FindSymbolsContainingAsync(qualifiedName, cancellationToken))
                .Where(symbol => symbol.FilePath == filePath && symbol.StartLine > startLine && symbol.EndLine <= endLine && symbol.QualifiedName.StartsWith(qualifiedName + ".", StringComparison.Ordinal))
                .OrderBy(symbol => symbol.StartLine).ToList()
            : [];
        var lines = new List<string>(signature);
        var bases = await ResolveTypeBasesAsync(qualifiedName, filePath, signature, root, cancellationToken);
        if (bases.Count > 0) lines.Add(TypeBases.FormatLine(bases));
        foreach (var member in members)
        {
            var memberName = member.QualifiedName[(qualifiedName.Length + 1)..];
            if (memberName.Contains('.')) continue;
            lines.Add($"{member.Kind.ToLowerInvariant()} {memberName} {member.StartLine}-{member.EndLine}" + OutlineSignature(member.Signature));
        }
        return (string.Join('\n', lines), members.Count(member => !member.QualifiedName[(qualifiedName.Length + 1)..].Contains('.')), bases);
    }

    private static readonly string[] BaseKinds = ["Class", "Type", "Record", "Interface", "Struct"];

    // Each declared base is resolved like `read` resolves a name (exact, module, suffix, short name); a base is
    // shown with its location only when exactly one indexed type answers to it. A dotted base that finds nothing
    // is retried by its last segment. Everything else stays unresolved ("-"): never a guess.
    private async Task<IReadOnlyList<TypeBase>> ResolveTypeBasesAsync(string qualifiedName, string filePath, List<string> declaration, string? root, CancellationToken cancellationToken)
    {
        var names = TypeBases.Parse(filePath, declaration);
        if (names.Count == 0) return [];
        var result = new List<TypeBase>();
        foreach (var name in names)
        {
            SymbolSearchResult? found = null;
            if (root is not null && _repositoryStore is ISymbolStore)
            {
                foreach (var attempt in name.Contains('.') ? new[] { name, name[(name.LastIndexOf('.') + 1)..] } : new[] { name })
                {
                    var resolution = await ReadableSymbolResolver.ResolveAsync(_repositoryStore, root, attempt, cancellationToken);
                    var types = resolution.Candidates
                        .Where(candidate => BaseKinds.Contains(candidate.Kind) && candidate.FilePath is not null && candidate.StartLine is not null && candidate.EndLine is not null
                            && !string.Equals(candidate.QualifiedName, qualifiedName, StringComparison.Ordinal))
                        .ToList();
                    if (types.Count == 1) { found = types[0]; break; }
                    if (types.Count > 1) break;
                }
            }
            result.Add(found is null ? new TypeBase(name) : new TypeBase(name, found.QualifiedName, found.FilePath, found.StartLine, found.EndLine));
        }
        return result;
    }

    private async Task<ProtocolResponse> HandleReadTypeAsync(ProtocolRequest request, SessionState session, CancellationToken cancellationToken)
    {
        var query = GetReadQuery(request.Payload);
        if (query is null)
        {
            return Fail("ValidationError", "Read type requests must include either a string 'query' field or an 'id' field.");
        }

        ReadableResolution? typeResolution = null;
        if (_repositoryStore is ISymbolStore)
        {
            var typeRoot = await _repositoryStore.GetRepositoryRootPathAsync(session.RepositoryId, cancellationToken);
            if (typeRoot is not null)
            {
                var indexed = await ReadableSymbolResolver.ResolveAsync(_repositoryStore, typeRoot, query, cancellationToken);
                indexed = indexed with { Candidates = indexed.Candidates.Where(candidate => candidate.Kind is "Class" or "Type" or "Record" or "Interface" or "Struct" or "Enum").ToList() };
                typeResolution = indexed;
                if (indexed.Candidates.Count > 1)
                {
                    var ranked = RankCandidates(indexed.Candidates, query);
                    var total = indexed.Candidates.Count;
                    return new ProtocolResponse(false, null, [], [new ProtocolError("AmbiguousMatch", $"Type '{query}' matched multiple definitions ({total}); pass a qualified name.", query, ranked.Select(item => item.QualifiedName).ToList(), ranked, total)]);
                }
                if (indexed.Candidates.Count == 1 && indexed.Candidates[0].FileStatus == "unsafe path")
                    return Fail("ValidationError", $"Indexed path for '{query}' contains a symbolic link or escapes the repository root.", query);
                if (indexed.Candidates.Count == 1 && indexed.Candidates[0].FileStatus == "file missing")
                    return Fail("FileMissing", $"file missing: {indexed.Candidates[0].FilePath}", query);
            }
        }

        if (TryGetId(request.Payload, out var id))
        {
            var typeSummary = await _repositoryStore.FindTypeSummaryByIdAsync(id, cancellationToken);
            if (typeSummary is null)
            {
                return Fail("NotFound", $"Type '{id}' was not found.");
            }

            var idRoot = await _repositoryStore.GetRepositoryRootPathAsync(session.RepositoryId, cancellationToken);
            if (idRoot is null) return Fail("RepositoryNotIndexed", "Repository root was not found.");
            if (!RepositoryPathGuard.TryResolve(idRoot, typeSummary.FilePath, out var idAbsolute, out var idPathError)) return Fail("ValidationError", idPathError, id);
            if (!IOFile.Exists(idAbsolute)) return Fail("FileMissing", $"file missing: {typeSummary.FilePath}", id);
            var idLines = (await IOFile.ReadAllTextAsync(idAbsolute, cancellationToken)).Replace("\r\n", "\n").Split('\n');
            var idEndLine = typeSummary.EndLine;
            var idSource = string.Join('\n', idLines[(typeSummary.StartLine - 1)..idEndLine]);
            var idReturned = idSource.Split('\n').Length;
            var (idOutline, idCount, idBases) = TryGetBoolean(request.Payload, "full") ? (idSource, 0, (IReadOnlyList<TypeBase>)[]) : await BuildTypeOutlineAsync(typeSummary.QualifiedName, typeSummary.FilePath, typeSummary.StartLine, typeSummary.EndLine, idLines, idRoot, cancellationToken);
            var idMetadata = TryGetBoolean(request.Payload, "full") ? (object)new { TypeName = typeSummary.Name, Namespace = typeSummary.Namespace, Project = typeSummary.Project, Kind = typeSummary.Kind, ResolvedVia = "exact" } : new { TypeName = typeSummary.Name, Namespace = typeSummary.Namespace, Project = typeSummary.Project, Kind = typeSummary.Kind, ResolvedVia = "exact", Outline = true, MemberCount = idCount, Bases = idBases, BodyLines = typeSummary.EndLine - typeSummary.StartLine + 1, Hint = "pass --full for the body" };
            var responseById = new SourceReadResponse("type", typeSummary.Id, typeSummary.QualifiedName, typeSummary.FilePath, typeSummary.StartLine, typeSummary.EndLine, typeSummary.EndLine - typeSummary.StartLine + 1, idOutline.Split('\n').Length, false, true, idOutline, idMetadata);
            await RecordHistoryAsync(() => _sessionStore.MarkSymbolExpandedAsync(new MarkSymbolExpandedRequest(session.Id, typeSummary.Id), cancellationToken));
            return new ProtocolResponse(true, JsonSerializer.SerializeToElement(responseById), [], []);
        }

        var types = await ResolveTypeSummariesAsync(query, request.Payload, cancellationToken);
        types = PreferExact(types, query, type => type.Id, type => type.QualifiedName, type => type.Name);
        if (types.Count == 0 && typeResolution?.Candidates.Count == 1)
        {
            var found = await _repositoryStore.FindTypeSummaryByIdAsync(typeResolution.Candidates[0].Id, cancellationToken);
            if (found is not null) types = [found];
        }
        if (types.Count == 0)
        {
            return await NotFoundWithSuggestionsAsync("Type", query, cancellationToken);
        }

        if (types.Count > 1)
        {
            var ranked = RankCandidates(types.Select(type => new SymbolSearchResult(type.Id, type.Kind, type.Name, type.QualifiedName, type.FilePath, type.StartLine, type.EndLine)).ToList(), query);
            var total = types.Count;
            return new ProtocolResponse(false, null, [], [new ProtocolError("AmbiguousMatch", $"Type '{query}' matched multiple definitions ({total}); pass a qualified name.", query, ranked.Select(item => item.QualifiedName).ToList(), ranked, total)]);
        }

        var type = types[0];
        var root = await _repositoryStore.GetRepositoryRootPathAsync(session.RepositoryId, cancellationToken);
        if (root is null) return Fail("RepositoryNotIndexed", "Repository root was not found.");
        if (!RepositoryPathGuard.TryResolve(root, type.FilePath, out var absolute, out var pathError)) return Fail("ValidationError", pathError, query);
        if (!IOFile.Exists(absolute)) return Fail("FileMissing", $"file missing: {type.FilePath}", query);
        var fileLines = (await IOFile.ReadAllTextAsync(absolute, cancellationToken)).Replace("\r\n", "\n").Split('\n');
        if (type.StartLine < 1 || type.EndLine > fileLines.Length) return Fail("StaleIndex", $"Indexed span is stale for {type.FilePath}; run devx index update.", query);
        var typeFull = TryGetBoolean(request.Payload, "full");
        var typeEndLine = type.EndLine;
        var bodySource = string.Join('\n', fileLines[(type.StartLine - 1)..typeEndLine]);
        var (classSource, memberCount, typeBases) = typeFull ? (bodySource, 0, (IReadOnlyList<TypeBase>)[]) : await BuildTypeOutlineAsync(type.QualifiedName, type.FilePath, type.StartLine, type.EndLine, fileLines, root, cancellationToken);
        var classLines = classSource.Split('\n').Length;
        var typeVia = typeResolution?.ResolvedVia ?? "exact";
        var requestedTypeLines = type.EndLine - type.StartLine + 1;
        var metadata = typeFull ? (object)new { TypeName = type.Name, Namespace = type.Namespace, Project = type.Project, Kind = type.Kind, ResolvedVia = typeVia } : new { TypeName = type.Name, Namespace = type.Namespace, Project = type.Project, Kind = type.Kind, ResolvedVia = typeVia, Outline = true, MemberCount = memberCount, Bases = typeBases, BodyLines = requestedTypeLines, Hint = "pass --full for the body" };
        var response = new SourceReadResponse("type", type.Id, type.QualifiedName, type.FilePath, type.StartLine, type.EndLine, requestedTypeLines, classLines, false, true, classSource, metadata);
        await RecordHistoryAsync(() => _sessionStore.MarkSymbolExpandedAsync(new MarkSymbolExpandedRequest(session.Id, type.Id), cancellationToken));
        return new ProtocolResponse(true, JsonSerializer.SerializeToElement(response), [], []);
    }

    private async Task<ProtocolResponse> HandleReadSectionAsync(ProtocolRequest request, SessionState session, CancellationToken cancellationToken)
    {
        if (!TryGetString(request.Payload, "file", out var file))
        {
            return Fail("ValidationError", "Read section requests must use 'file#heading'.");
        }

        // The name is optional. `read section <file>#` asks for the file, and on a source file
        // `read section <file>#<name>` asks for a symbol -- both are how a caller actually
        // typed it, and both used to be answered with an error.
        var heading = TryGetString(request.Payload, "heading", out var providedHeading) ? providedHeading : string.Empty;
        if (string.IsNullOrWhiteSpace(heading))
        {
            return await ReadSectionTargetAsync(request, session, file, string.Empty, cancellationToken);
        }

        if (!Path.GetExtension(file).Equals(".md", StringComparison.OrdinalIgnoreCase))
        {
            return await ReadSectionTargetAsync(request, session, file, heading, cancellationToken);
        }

        var sectionRoot = await _repositoryStore.GetRepositoryRootPathAsync(session.RepositoryId, cancellationToken);
        if (string.IsNullOrWhiteSpace(sectionRoot))
        {
            return Fail("RepositoryNotIndexed", "Repository root was not found.");
        }
        if (!RepositoryPathGuard.TryResolve(sectionRoot, file, out var sectionPath, out var sectionPathError))
            return Fail("ValidationError", sectionPathError, $"{file}#{heading}");
        if (!IOFile.Exists(sectionPath)) return Fail("FileMissing", $"file missing: {file}", $"{file}#{heading}");

        if (_repositoryStore is not IRootScopedFileSnapshotStore rootScopedSnapshotStore)
        {
            return Fail("UnsupportedStoreCapability", "The repository store cannot scope a file snapshot to this repository.", file);
        }
        var relativeSectionPath = Path.GetRelativePath(Path.GetFullPath(sectionRoot), sectionPath).Replace('\\', '/');
        var snapshot = await rootScopedSnapshotStore.GetFileSnapshotAsync(session.RepositoryId, sectionRoot, relativeSectionPath, cancellationToken);
        if (snapshot is null)
        {
            return Fail("NotFound", $"Markdown file '{file}' was not found.", file);
        }

        var exactCandidates = snapshot.Methods
            .Where(method => string.Equals(method.Name, heading, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var candidates = exactCandidates.Count > 0
            ? exactCandidates
            : snapshot.Methods.Where(method => method.Name.StartsWith(heading, StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0)
        {
            var suggestions = snapshot.Methods
                .Where(method => method.Name.Contains(heading, StringComparison.OrdinalIgnoreCase) || heading.Contains(method.Name, StringComparison.OrdinalIgnoreCase))
                .Select(method => $"{file}#{method.Name}")
                .Take(5)
                .ToList();
            return Fail("NotFound", $"Heading '{heading}' was not found in '{file}'.", $"{file}#{heading}", suggestions);
        }

        if (candidates.Count > 1)
        {
            return Fail(
                "AmbiguousMatch",
                $"Heading prefix '{heading}' matched multiple sections in '{file}'.",
                $"{file}#{heading}",
                candidates.Select(method => $"{file}#{method.Name}"));
        }

        var selectedHeading = candidates[0].Name;
        // Resolve through the repository-scoped symbol path. The legacy name+path method
        // lookup is global and can select another repository's same-relative-path source.
        return await ReadSectionTargetAsync(request, session, file, selectedHeading, cancellationToken);
    }

    // The forgiving half of `read section`. A source file is not a document of headings, so
    // `<file>#<name>` resolves <name> as a symbol in that file and answers with the same
    // response `read method` gives, saying which symbol it resolved. `<file>#` with no name
    // answers with the file, capped and paged exactly as `read file` does.
    private async Task<ProtocolResponse> ReadSectionTargetAsync(ProtocolRequest request, SessionState session, string file, string name, CancellationToken cancellationToken)
    {
        var root = await _repositoryStore.GetRepositoryRootPathAsync(session.RepositoryId, cancellationToken);
        if (string.IsNullOrWhiteSpace(root))
        {
            return Fail("RepositoryNotIndexed", "Repository root was not found.");
        }

        if (!RepositoryPathGuard.TryResolve(root, file, out var absolute, out var pathError))
        {
            return Fail("ValidationError", pathError);
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            if (!IOFile.Exists(absolute)) return Fail("FileMissing", $"file missing: {file}", $"{file}#");
            var fileRequest = new ProtocolRequest(request.Version, "READ_FILE", session.Id.Value.ToString(),
                JsonSerializer.SerializeToElement(new { query = file, full = false, range = $"1:{int.MaxValue}" }), request.AgentLabel);
            return await HandleReadFileAsync(fileRequest, session, cancellationToken);
        }

        var resolution = await ReadableSymbolResolver.ResolveAsync(_repositoryStore, root, name, cancellationToken);
        var inFile = resolution.Candidates
            .Where(candidate => candidate.FilePath is not null
            && RepositoryPathGuard.TryResolve(root, candidate.FilePath.Replace('\\', '/'), out var candidatePath, out _)
            && string.Equals(candidatePath, absolute, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            .DistinctBy(candidate => candidate.Id)
            .ToList();

        if (inFile.Count == 0)
        {
            var suggestions = (await FileSymbolNamesAsync(file, cancellationToken))
                .Select(symbol => $"devx read section \"{file}#{symbol}\"")
                .Take(5)
                .ToList();
            if (suggestions.Count == 0)
            {
                suggestions = resolution.Candidates
                    .Select(candidate => $"devx read method \"{candidate.QualifiedName}\"")
                    .Take(5)
                    .ToList();
            }

            return Fail("NotFound", $"'{name}' is not a symbol in '{file}'.", $"{file}#{name}", suggestions);
        }

        if (inFile.Count > 1)
        {
            return Fail(
                "AmbiguousMatch",
                $"'{name}' matched multiple symbols in '{file}'.",
                $"{file}#{name}",
                inFile.Select(candidate => $"devx read method \"{candidate.QualifiedName}\""));
        }

        var symbol = inFile[0];
        var symbolRequest = new ProtocolRequest(request.Version, "READ_METHOD", session.Id.Value.ToString(),
            JsonSerializer.SerializeToElement(new { query = symbol.QualifiedName }), request.AgentLabel);
        var response = await HandleReadMethodAsync(symbolRequest, session, cancellationToken);
        if (!response.Success || response.Data is not { ValueKind: JsonValueKind.Object } data)
        {
            return response;
        }

        // `read method` already returns the right bytes; the section caller only needs the
        // response to say which symbol it resolved to, which the metadata carries.
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in data.EnumerateObject()) fields[property.Name] = property.Value.Clone();
        fields["Metadata"] = JsonSerializer.SerializeToElement(new
        {
            Heading = symbol.Name,
            RequestedHeading = name,
            ResolvedVia = "section-symbol",
            ResolvedSymbol = symbol.QualifiedName,
            SymbolKind = symbol.Kind,
            Message = $"read section '{file}#{name}' resolved to {symbol.Kind.ToLowerInvariant()} {symbol.QualifiedName}",
        });
        return new ProtocolResponse(true, JsonSerializer.SerializeToElement(fields), response.Diagnostics, []);
    }

    private async Task<IReadOnlyList<string>> FileSymbolNamesAsync(string file, CancellationToken cancellationToken)
    {
        var snapshot = await _repositoryStore.GetFileSnapshotAsync(file, cancellationToken);
        if (snapshot is null) return [];
        return snapshot.Types.Select(type => type.Name)
            .Concat(snapshot.Methods.Select(method => method.Name))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }


    private static string? GetReadQuery(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (payload.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String)
        {
            return idElement.GetString();
        }

        if (payload.TryGetProperty("query", out var queryElement) && queryElement.ValueKind == JsonValueKind.String)
        {
            return queryElement.GetString();
        }

        return null;
    }

    private async Task<IReadOnlyList<MethodSource>> ResolveMethodSourcesAsync(string query, JsonElement payload, CancellationToken cancellationToken)
    {
        if (TryGetId(payload, out var id))
        {
            var methods = await _repositoryStore.FindMethodSourcesAsync(id, cancellationToken);
            return methods.Where(method => string.Equals(method.Id, id, StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        return await LookupMethodSourcesAsync(query, cancellationToken);
    }

    private Task<IReadOnlyList<MethodSource>> LookupMethodSourcesAsync(string query, CancellationToken cancellationToken)
    {
        // Qualified Python module queries do not contain the synthetic "<module>" container used
        // by the index. Search broadly by the terminal method name, then apply strict path/container
        // resolution in ResolveSafeMethodMatches.
        var lookup = query.Contains('.') && !Guid.TryParse(query, out _)
            ? query[(query.LastIndexOf('.') + 1)..]
            : query;
        return _repositoryStore.FindMethodSourcesAsync(lookup, cancellationToken);
    }

    private async Task<IReadOnlyList<TypeSummary>> ResolveTypeSummariesAsync(string query, JsonElement payload, CancellationToken cancellationToken)
    {
        if (TryGetId(payload, out var id))
        {
            query = id;
        }

        return await _repositoryStore.FindTypeSummariesAsync(query, cancellationToken);
    }

    private static bool TryGetId(JsonElement payload, out string id)
    {
        id = string.Empty;
        return payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("id", out var idElement)
            && idElement.ValueKind == JsonValueKind.String
            && (id = idElement.GetString() ?? string.Empty).Length > 0;
    }
}
