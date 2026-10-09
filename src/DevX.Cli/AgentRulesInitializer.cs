using System.Text;

namespace DevX.Cli;

public sealed record AgentRulesUpdate(string Agent, string Path, bool ReplacedExistingBlock);

public static class AgentRulesInitializer
{
    public const string StartMarker = "<!-- DEVX RULES START -->";
    public const string EndMarker = "<!-- DEVX RULES END -->";

    public static async Task<IReadOnlyList<AgentRulesUpdate>> InitializeAsync(
        string repositoryRoot,
        IReadOnlyList<string> agents,
        bool global,
        CancellationToken cancellationToken = default)
    {
        var selectedAgents = agents.Count == 0 ? new[] { "claude", "codex" } : agents;
        var updates = new List<AgentRulesUpdate>();
        foreach (var agent in selectedAgents.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var normalized = agent.Trim().ToLowerInvariant();
            var path = ResolvePath(repositoryRoot, normalized, global);
            var existing = File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : string.Empty;
            var replaced = existing.Contains(StartMarker, StringComparison.Ordinal);
            var updated = UpsertBlock(existing, BuildBlock(normalized));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, updated, cancellationToken);
            updates.Add(new AgentRulesUpdate(normalized, path, replaced));
        }

        return updates;
    }

    private static string ResolvePath(string repositoryRoot, string agent, bool global)
    {
        if (agent is not ("claude" or "codex"))
        {
            throw new ArgumentException($"Unsupported agent '{agent}'. Supported agents: claude, codex.");
        }

        if (!global) return Path.Combine(repositoryRoot, agent == "claude" ? "CLAUDE.md" : "AGENTS.md");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, agent == "claude" ? ".claude/CLAUDE.md" : ".codex/AGENTS.md");
    }

    private static string UpsertBlock(string existing, string block)
    {
        var start = existing.IndexOf(StartMarker, StringComparison.Ordinal);
        var end = existing.IndexOf(EndMarker, StringComparison.Ordinal);
        if ((start >= 0) != (end >= 0) || (start >= 0 && end < start))
        {
            throw new InvalidOperationException("The instruction file contains an incomplete DevX rules block; repair its markers before retrying.");
        }

        if (start >= 0)
        {
            end += EndMarker.Length;
            return existing[..start] + block + existing[end..];
        }

        if (string.IsNullOrWhiteSpace(existing)) return block + Environment.NewLine;
        return existing.TrimEnd() + Environment.NewLine + Environment.NewLine + block + Environment.NewLine;
    }

    private static string BuildBlock(string agent)
    {
        var builder = new StringBuilder();
        builder.AppendLine(StartMarker);
        builder.AppendLine("## DevX code workflow");
        builder.AppendLine();
        builder.AppendLine("Use DevX (`devx`) as the primary code-intelligence tool in this workspace.");
        builder.AppendLine();
        builder.AppendLine("Use the session ID your brief gives you; create one only if none was given:");
        builder.AppendLine("```bash");
        builder.AppendLine("SESSION_ID=$(devx session create --format json | jq -r '.Data.sessionId // .sessionId')");
        builder.AppendLine("```");
        builder.AppendLine();
        builder.AppendLine($"`--agent <label>`: use the label your brief gives you (for example, `deepseek`); default to `{agent}`.");
        builder.AppendLine();
        builder.AppendLine("```bash");
        builder.AppendLine($"devx search symbol \"<name>\" --session \"$SESSION_ID\" --agent {agent}");
        builder.AppendLine($"devx search text \"<text>\" --session \"$SESSION_ID\" --agent {agent}");
        builder.AppendLine($"devx read method \"<symbol>\" \"<another symbol>\" --session \"$SESSION_ID\" --agent {agent}   # several names in one call");
        builder.AppendLine($"devx read section 'path/to/file.md#Heading' --session \"$SESSION_ID\" --agent {agent}");
        builder.AppendLine($"devx replace <file> --old \"<text>\" --new \"<text>\" --verify --session \"$SESSION_ID\" --agent {agent}   # edit + build + tests, ONE call");
        builder.AppendLine($"devx patch create <out.json> --op ReplaceMethod --path <file> --session \"$SESSION_ID\" --agent {agent}");
        builder.AppendLine($"devx verify [build|test|both] --session \"$SESSION_ID\" --agent {agent}   # check without editing; `verify build` is build-only");
        builder.AppendLine("```");
        builder.AppendLine();
        builder.AppendLine("The four steps are: find it (`search`), read just it (`read`), change it (`replace` or `patch`), then check it. Make your edits, add `--verify` to the LAST one (or run `devx verify` once) -- one check for the batch, not one per edit.");
        builder.AppendLine();
        builder.AppendLine("Do not spend a second call on what the first one already answered. A `devx search` that matches exactly one symbol returns its source with it, so go straight to the edit; a `devx search text` result already carries the matching line, so read the file only when you need the lines around it.");
        builder.AppendLine();
        builder.AppendLine("Every edit re-indexes the files it touches by itself -- never run `index update` after a write (`--no-index` opts out, and you almost never want that). Use `search text` before grep for repository content. Prefer narrow symbol, method, section, and line-range reads over whole-file reads. If an older brief says `devx`, run the equivalent `devx` command.");
        builder.Append(EndMarker);
        return builder.ToString();
    }
}
