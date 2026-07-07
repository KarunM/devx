namespace Ecp.Cli;

public sealed record AskTraceDocument(string SessionId, string UserPrompt, string Model, List<AskTraceRound> Rounds);

public sealed record AskTraceRound(
    int RoundNumber,
    string Model,
    string Prompt,
    string RawResponse,
    string ParsedKind,
    string? ResponseReason,
    IReadOnlyList<AskTraceContextRequestItem> ContextRequests,
    IReadOnlyList<AskTraceOperation> ExecutedOperations,
    IReadOnlyList<string> ContextSummaries,
    string AccumulatedContextSummary);

public sealed record AskTraceContextRequestItem(string Type, string? Query, string? Path, string? Range, string? Id, string? Reason);

public sealed record AskTraceOperation(string Operation, string? Query, string? Path, string? Range, string? Id, string Summary);

public sealed record AskTraceContextSummary(string Kind, string Summary, string? Query, IReadOnlyList<string> FilePaths, IReadOnlyList<string> SymbolNames, IReadOnlyList<string> QualifiedNames, IReadOnlyList<string> Identifiers, int LineCount, string? Path, string? Range, bool Success, int CharacterCount, bool AppendedToContext = false);

public sealed record AskRepoMap(IReadOnlyList<AskRepoMapFile> Files, IReadOnlyList<AskRepoMapSymbol> Symbols, string RenderedMap)
{
    public int FileCount => Files.Count;
    public int SymbolCount => Symbols.Count;
}

public sealed record AskRepoMapFile(string Path, string RoleHint);
public sealed record AskRepoMapSymbol(string Path, string Name, string QualifiedName);
