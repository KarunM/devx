using DevX.Application;
using DevX.Domain;
using DevX.Cli;
using DevX.Infrastructure.Storage.SQLite;
using DevX.Indexer;
using DevX.Protocol;

static partial class Program
{
    // Options whose next token is their VALUE, not a command. `devx --session <id> --help`
    // must not read the session id as a command name.
    static bool HelpScanValueOption(string token) => token switch
    {
        "--session" or "--agent" or "--task" or "--format" or "--op" or "--path"
            or "--content" or "--content-file" or "--replacement" or "--replacement-file"
            or "--method" or "--method-text" or "--method-text-file" or "--position"
            or "--namespace" or "--containing-type" or "--type-id" or "--occurrence"
            or "--old" or "--old-text" or "--new" or "--output" or "--input" or "--period"
            or "--blocked-terms" or "--runtime" or "--target" or "--timeout" or "--org"
            or "--team" or "--repo" or "--name" or "--capability" or "--failed-command"
            or "--reason" or "--action" or "--command" or "--file" or "--coverage-start"
            or "--coverage-end" or "--range" => true,
        _ => false
    };

    // The two-word command forms, so `devx patch create --help` reaches the patch help while
    // `devx search Foo --help` is left alone (Foo is a query, not a subcommand).
    static bool TwoWordCommand(string two) => two switch
    {
        "patch create" or "patch preview" or "patch validate" or "patch apply" or "patch rollback"
            or "index status" or "index update" or "index changed" or "artifact create"
            or "artifact delete" or "artifact path" or "search symbol" or "search text"
            or "read method" or "read type" or "read section" or "report html" or "report export"
            or "fallback record" or "fallback observe" or "session create" or "session list" => true,
        _ => false
    };

    // --help has to work wherever it sits among the leading options -- RULES_B tells agents to
    // pass --session/--agent/--task on every call, and on 1.6.10 that turned `devx --help` into
    // "Unknown command '--session'". It must still NOT hijack a prompt that merely mentions it
    // (`devx ask "explain --help"`), so the scan stops as soon as real argument text begins.
    static bool WantsHelp(string[] args, out string command)
    {
        command = "";
        var words = 0;
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (token is "--help" or "-h")
            {
                return true;
            }
            if (token.StartsWith('-'))
            {
                if (HelpScanValueOption(token))
                {
                    i++;
                }
                continue;
            }
            words++;
            if (words == 1)
            {
                command = token;
                continue;
            }
            if (words == 2 && TwoWordCommand($"{command} {token}"))
            {
                command = $"{command} {token}";
                continue;
            }
            return false;
        }
        return false;
    }

    // The commands an agent actually reaches for. Used only to turn a typo into a fix.
    static string? ClosestCommand(string command)
    {
        string[] KnownCommands =
        {
            "init", "index", "search", "read", "snapshot", "session", "capabilities",
            "test", "build", "verify", "patch", "replace", "mcp", "ask", "report", "version"
        };
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var candidate in KnownCommands)
        {
            var distance = Levenshtein(command, candidate);
            if (distance < bestDistance) { bestDistance = distance; best = candidate; }
        }
        // Only suggest an actual neighbour; "frobnicate" should get the help line instead.
        return bestDistance <= Math.Max(2, command.Length / 3) ? best : null;
    }

    static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    // Deliberately short: this is the whole point of it existing next to PrintHelp. The edit
    // line is the one that matters -- on 1.6.10 it named only `patch`, so agents guessed at op
    // names instead of using `devx replace`, which is what they wanted.
    static void PrintAgentHelp()
    {
        Console.WriteLine("devx cheat sheet (add --session <id>; piped output is already agent format)");
        Console.WriteLine("  --format json still gives the full JSON shape; --format text gives the human text");
        Console.WriteLine("  devx index .                      index the repo (or: index update <files>)");
        Console.WriteLine("  devx search symbol <name>         find it; one hit returns its source too");
        Console.WriteLine("  devx search text <literal>        literal text search");
        Console.WriteLine("  devx read method \"<Qualified>\"    read ONE function; \"<file#Heading>\" for a doc section");
        Console.WriteLine("  devx read <Name> <Name>          several symbols in ONE call, not one call each");
        Console.WriteLine("  devx read type \"<Class>\"         declaration + bases + member outline (--full = body); bare names resolve when unique, no search first");
        Console.WriteLine("  devx callers|callees|references \"<Qualified>\"   who calls / what it calls / where used: use before `search text`");
        Console.WriteLine("  devx snapshot <file>              map a file: classes + functions, no bodies");
        Console.WriteLine("  EDIT -- pick one:");
        Console.WriteLine("  devx replace <file> --old <text> --new <text>   one text edit (--all | --occurrence n)");
        Console.WriteLine("  devx patch create|validate|apply  whole-function edits by name (--op; see --help)");
        Console.WriteLine("  then check it in ONE call: add --verify to the LAST edit, or run `devx verify`");
        Console.WriteLine("  devx verify [build|test|both]     build + tests, one summary (`verify build` = build only)");
        Console.WriteLine("  devx capabilities                 what this build can do");
        if (IsAskConfigured(Environment.CurrentDirectory))
        {
            Console.WriteLine("  devx ask \"<question>\"             ask the configured LLM about this repo");
        }
    }

    static void PrintHelp()
    {
        Console.WriteLine("""
1. devx search <name>   (symbols; falls back to text)
2. devx read <name> [more names]   (source of a symbol, file, or file#Heading)
Use `devx read method <name> --context` for bounded test/name candidates when related code needs discovery.
3. devx replace <file> --old <text> --new <text> --verify   (edit, then build + tests in the same call)
4. devx verify [build|test|both]   (check without editing; build + tests, one summary)
Add --format agent for compact output; no session needed.
`devx read type <Class>` shows the declaration, a `bases:` line with each base's path (follow a hierarchy one read type per level, no search in between) and a member outline (--full for the body); bare or Class.member names resolve when unique, so skip the search when you know the name.
For "who calls / what does it call / where is it used" use `devx callers|callees <method>` and `devx references <symbol>` before `search text`; `devx snapshot <file>` outlines a file.

Usage:
  devx version [--format json]
  devx --version
  devx init [--agents claude,codex] [--global] [--format json]
  devx index <path>
  devx index status [--session <id>]
  devx index update <file> [<file> ...] --session <id>
  devx index changed --session <id>
  devx search <query> [--session <id>] [--agent <label>]
  devx search <symbol|text> <query> [--session <id>] [--agent <label>]
  devx snapshot <file> --session <id> [--agent <label>]
  devx callers <method> | devx callees <method> | devx references <symbol> --session <id>   (cross-references; use instead of text search)
  devx callers <method> --full   (many call sites are grouped by function; --full lists every site)
  devx read <name|file#Heading|file> [<name> ...] [--session <id>]
  devx read <file|method|type|section> <target> [--range start:end|--full] [--session <id>]
  devx artifact create <name> --content <text>|--content-file <path>|--content-json <json-string>|--content-stdin
  devx artifact delete <name>
  devx artifact path <name>
  devx patch create <output-file> --session <id> --op <Operation> --path <relative-path> [options] [--append]
  devx replace <file> --old <text> --new <text> [--all | --occurrence <n>] [--verify[=build|test|both]]
  Editing: simple edit -> devx replace; structured/multi-file edits -> devx patch
  devx patch preview <patch-file> --session <id>
  devx patch validate <patch-file> --session <id>
  devx patch apply <patch-file> --session <id> [--verify[=build|test|both]]
  devx patch rollback <patch-file> --session <id>
  devx health [--session <id>]
  devx doctor [--session <id>]
  devx build --session <id> [--runtime <kind>] [--target <path|name>]
  devx test [<file> ...] --session <id> [--runtime <kind>] [--target <path|name>] [--timeout <seconds>]
  devx verify [build|test|both] [<file|dir> ...] [--runtime <kind>] [--target <path|name>] [--timeout <seconds>]
  devx report html [--period 30d] [--session <id>] [--output <file>] [--format html|json] [--anonymize] [--blocked-terms <file>]
  devx report --session <id> --format json [--anonymize] [--blocked-terms <file>]
  devx report html --input <events.json> [--org <name>] [--team <name>] [--repo <name>]
  devx report html --input <a.json> --input <b.json> [--period 30d] [--output <file>]
  devx report export [--period 30d] [--session <id>] --output <events.json>
  devx fallback record --session <id> --capability <id> --failed-command <command> --reason <reason> --action <fallback-action>
  devx fallback observe --session <id> --agent <label> --command <shell-command> [--file <path> ...]
  devx fallback observe --session <id> --agent <label> --coverage-start <ISO8601> --coverage-end <ISO8601>
  devx capabilities [--format json]
  devx mcp serve

Patch create options:
  --op <Operation>
  --path <relative-path>
  --append
  --content <text>
  --content-file <path>
  --content-stdin
  --replacement <text>
  --replacement-file <path>
  --replacement-stdin
  --method <name>
  --method-text <text>
  --method-text-file <path>
  --position <value>
  --namespace <name>
  --containing-type <name>
  --type-id <id>

Artifact options:
  --content <text>
  --content-file <path>
  --content-json <json-string>
  --content-stdin

Global options:
  --format <json|text|agent>
  --session <id>
  --agent <label>
  --task <label>   Attribute recorded calls to a brief/task (caller supplied)

Notes:
  build, test, and verify find the repository's session themselves -- --session is optional.
  report requires explicit --session selection.
""");
        // `ask` only exists for a repo that configured an endpoint, so it is advertised only
        // there -- an unconfigured repo gets no pointer to a command that will refuse to run.
        if (IsAskConfigured(Environment.CurrentDirectory))
        {
            Console.WriteLine("  devx ask \"<question>\"   ask the LLM configured in .devx/config.json");
        }
    }
}
