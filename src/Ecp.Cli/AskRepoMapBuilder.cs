using System.Text;
using Ecp.Domain;
using Ecp.Protocol;

namespace Ecp.Cli;

public static class AskRepoMapBuilder
{
    public static async Task<AskRepoMap> BuildAsync(IRepositoryStore store, SessionState session, CancellationToken cancellationToken = default)
    {
        var repositoryRoot = await store.GetRepositoryRootPathAsync(session.RepositoryId, cancellationToken);
        var effectiveRoot = repositoryRoot ?? Environment.CurrentDirectory;

        var files = new List<AskRepoMapFile>();
        var symbols = new List<AskRepoMapSymbol>();
        var rendered = new StringBuilder();

        rendered.AppendLine(".");
        foreach (var file in await store.SearchFilesAsync(string.Empty, cancellationToken))
        {
            var roleHint = file.Path.EndsWith("Program.cs", StringComparison.OrdinalIgnoreCase)
                ? "source"
                : "source";
            files.Add(new AskRepoMapFile(file.Path, roleHint));
            rendered.AppendLine($"- {file.Path} :: {roleHint}");
        }

        foreach (var symbol in await store.FindReferencesAsync(string.Empty, cancellationToken))
        {
            symbols.Add(new AskRepoMapSymbol(symbol.FilePath, symbol.SymbolName, symbol.QualifiedName));
        }

        return new AskRepoMap(files, symbols, rendered.ToString());
    }
}