namespace DevX.Cli;

/// <summary>
/// The patch operations this build can actually apply, and the flags each one reads.
///
/// This exists because of the 1.6.10 benchmark regression: an agent that had never been
/// told the edit syntax invented ops ('replace', 'replace_text', 'replace-text', 'update')
/// and only learned they were wrong at `patch validate`. The catalog is the single place
/// that answers "is this op real?" — `patch create` now refuses an unknown op immediately
/// and points at `devx replace`, which is the tool the agent actually wanted.
///
/// The names here must match <c>PatchLifecycleService.ApplyOperation</c>'s switch exactly
/// (Ordinal — that switch is case-sensitive, so accepting 'replacemethod' here would only
/// move the failure one step later).
/// </summary>
public static class PatchOperationCatalog
{
    public sealed record PatchOperationInfo(string Name, string Flags, string Summary);

    public static readonly PatchOperationInfo[] All =
    [
        new("CreateFile", "--content <text> | --content-file <path> | --content-stdin",
            "write a new file"),
        new("DeleteFile", "(no flags)", "delete a file"),
        new("ReplaceMethod", "--method <name> --replacement <text> | --replacement-file <path>",
            "swap a whole function/type by name"),
        new("ReplaceText", "--old-text <text> [--replacement <text>] [--occurrence <n> | --all]",
            "replace a literal span (alias: TextReplace)"),
        new("InsertMethod", "--method-text <text> | --method-text-file <path> --position \"After:<Name>\"",
            "insert a function/type at a named boundary"),
        new("AddUsing", "--namespace <name>", "add a using (C# only)"),
        new("InsertText", "--content <text> --position \"After:<Name>\"", "insert text at a boundary"),
        new("ReplaceLineRange", "--content <text> --position <start:end>", "replace a line range"),
    ];

    public static string[] Names => All.Select(op => op.Name).ToArray();

    /// <summary>
    /// Spellings that already work and must keep working, but are not advertised as their
    /// own op. TextReplace has been an accepted synonym since before 1.6.10; this release
    /// adds no new aliases, it just refuses to break the one that existed.
    /// </summary>
    private static readonly string[] ExistingAliases = ["TextReplace"];

    /// <summary>True when ApplyOperation will accept this op spelling, byte for byte.</summary>
    public static bool IsKnown(string? operation) =>
        operation is not null
        && (All.Any(op => string.Equals(op.Name, operation, StringComparison.Ordinal))
            || ExistingAliases.Contains(operation, StringComparer.Ordinal));

    /// <summary>The message `patch create` prints for an op this build cannot apply.</summary>
    public static string UnknownOpMessage(string operation)
    {
        var text = $"unknown op '{operation}'; for a text edit use: "
                   + "devx replace <file> --old <text> --new <text>"
                   + Environment.NewLine
                   + "Supported ops: " + string.Join(", ", Names) + ".";
        if (Closest(operation) is { } near)
        {
            text += Environment.NewLine + $"Did you mean '{near}'?";
        }
        return text;
    }

    /// <summary>A near-miss spelling of a real op, or null when the input is nothing like one.</summary>
    public static string? Closest(string operation)
    {
        // 'replace_text' and 'replace-text' differ from 'ReplaceText' only in punctuation
        // and case, so compare on letters and digits alone.
        static string Normalize(string value) =>
            new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        var wanted = Normalize(operation);
        if (wanted.Length == 0)
        {
            return null;
        }
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var op in All)
        {
            var distance = Levenshtein(wanted, Normalize(op.Name));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = op.Name;
            }
        }
        return bestDistance <= Math.Max(2, wanted.Length / 3) ? best : null;
    }

    /// <summary>The ops block `devx patch create --help` prints.</summary>
    public static string HelpText()
    {
        var lines = new List<string>
        {
            "devx patch create <output-file> --session <id> --op <Operation> --path <relative-path> [options]",
            "Editing a whole function or type by name: patch create -> patch validate -> patch apply.",
            "For a one-off text edit use `devx replace <file> --old <text> --new <text>` instead.",
            "",
            "Operations:",
        };
        var width = All.Max(op => op.Name.Length);
        lines.AddRange(All.Select(op => $"  {op.Name.PadRight(width)}  {op.Flags}   -- {op.Summary}"));
        lines.Add("");
        lines.Add("Other options:");
        lines.Add("  --append                add this operation to an existing patch artifact");
        lines.Add("  --containing-type <name>   InsertMethod: the enclosing type");
        lines.Add("  --type-id <id>             InsertMethod: the enclosing type id");
        lines.Add("  --method-text-stdin        InsertMethod: method body from stdin");
        lines.Add("  --replacement-stdin        ReplaceMethod/ReplaceText: replacement from stdin");
        lines.Add("  --old <text>               ReplaceText: synonym for --old-text");
        return string.Join(Environment.NewLine, lines);
    }

    private static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }
}
