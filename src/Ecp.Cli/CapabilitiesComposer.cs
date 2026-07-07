namespace Ecp.Cli;

public sealed record CapabilityOption(string Name, string Description, string? Default, bool Required);
public sealed record CapabilitySubcommand(string Name, string Description, string[] Args);
public sealed record CapabilityCommand(string Name, string Description, string Usage, CapabilitySubcommand[] Subcommands, string[] Args, string[] Options, bool SupportsJsonOutput);
public sealed record CapabilitiesResponse(string Version, CapabilityOption[] GlobalOptions, CapabilityCommand[] Commands);

public static class CapabilitiesComposer
{
    public static CapabilitiesResponse Compose()
    {
        return new CapabilitiesResponse(
            "1.0",
            new[]
            {
                new CapabilityOption("--format", "Output format (json or text)", "text", false),
                new CapabilityOption("--session", "Session identifier for session-scoped commands", null, false)
            },
            new[]
            {
                new CapabilityCommand(
                    "ask",
                    "Ask the LLM to orchestrate code changes via tool calls",
                    "ecp ask <prompt> --session <id>",
                    Array.Empty<CapabilitySubcommand>(),
                    new[] { "<prompt>" },
                    new[] { "--session" },
                    true
                ),
                new CapabilityCommand(
                    "index",
                    "Index a repository for discovery and search",
                    "ecp index <path> | ecp index status [--session <id>] | ecp index update <file> --session <id>",
                    new[]
                    {
                        new CapabilitySubcommand("status", "Check index status for a repository", new[] { "--session" }),
                        new CapabilitySubcommand("update", "Re-index a single file after changes", new[] { "<file>" })
                    },
                    new[] { "<path>" },
                    Array.Empty<string>(),
                    true
                ),
                new CapabilityCommand(
                    "search",
                    "Search indexed symbols or full text",
                    "ecp search <symbol|text> <query> --session <id>",
                    Array.Empty<CapabilitySubcommand>(),
                    new[] { "<kind>", "<query>" },
                    new[] { "--session" },
                    true
                ),
                new CapabilityCommand(
                    "session",
                    "Session management",
                    "ecp session create",
                    new[] { new CapabilitySubcommand("create", "Create a new session for the current repository", Array.Empty<string>()) },
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    true
                ),
                new CapabilityCommand(
                    "artifact",
                    "Create or manage reusable patch payload artifacts",
                    "ecp artifact <create|delete|path> <name> [options]",
                    new[]
                    {
                        new CapabilitySubcommand("create", "Create an artifact payload", new[] { "<name>" }),
                        new CapabilitySubcommand("delete", "Delete an artifact payload", new[] { "<name>" }),
                        new CapabilitySubcommand("path", "Print the artifact path", new[] { "<name>" })
                    },
                    new[] { "<subcommand>", "<name>" },
                    new[] { "--content", "--content-file", "--content-json", "--content-stdin" },
                    true
                ),
                new CapabilityCommand(
                    "patch",
                    "Create, preview, validate, apply, or rollback a patch file",
                    "ecp patch <create|apply|preview|validate|rollback> <patch-file> --session <id>",
                    new[]
                    {
                        new CapabilitySubcommand("create", "Create a patch artifact", new[] { "<output-file>" }),
                        new CapabilitySubcommand("apply", "Apply a patch file", new[] { "<patch-file>" }),
                        new CapabilitySubcommand("preview", "Preview a patch without applying", new[] { "<patch-file>" }),
                        new CapabilitySubcommand("validate", "Validate a patch file", new[] { "<patch-file>" }),
                        new CapabilitySubcommand("rollback", "Rollback a previously applied patch", new[] { "<patch-file>" })
                    },
                    new[] { "<action>", "<patch-file>" },
                    new[] { "--session", "--op", "--path", "--append", "--content", "--content-file", "--content-stdin", "--replacement", "--replacement-file", "--replacement-stdin", "--method", "--method-text", "--method-text-file", "--position", "--namespace", "--containing-type", "--type-id" },
                    true
                ),
                new CapabilityCommand(
                    "build",
                    "Build the indexed solution",
                    "ecp build --session <id>",
                    Array.Empty<CapabilitySubcommand>(),
                    Array.Empty<string>(),
                    new[] { "--session" },
                    true
                ),
                new CapabilityCommand(
                    "test",
                    "Run tests for the indexed solution",
                    "ecp test --session <id>",
                    Array.Empty<CapabilitySubcommand>(),
                    Array.Empty<string>(),
                    new[] { "--session" },
                    true
                ),
                new CapabilityCommand(
                    "diagnostics",
                    "Get diagnostics for the indexed solution",
                    "ecp diagnostics --session <id>",
                    Array.Empty<CapabilitySubcommand>(),
                    Array.Empty<string>(),
                    new[] { "--session" },
                    true
                ),
                new CapabilityCommand(
                    "snapshot",
                    "Get a structural snapshot of a file",
                    "ecp snapshot <file> --session <id>",
                    Array.Empty<CapabilitySubcommand>(),
                    new[] { "<file>" },
                    new[] { "--session" },
                    true
                ),
                new CapabilityCommand(
                    "method",
                    "Get method summary or full details",
                    "ecp method <summary|get> <method> --session <id>",
                    new[]
                    {
                        new CapabilitySubcommand("summary", "Get a summary of the method", new[] { "<method>" }),
                        new CapabilitySubcommand("get", "Get full method details", new[] { "<method>" })
                    },
                    new[] { "<action>", "<method>" },
                    new[] { "--session" },
                    true
                ),
                new CapabilityCommand(
                    "type",
                    "Get type information",
                    "ecp type get <type> --session <id>",
                    new[] { new CapabilitySubcommand("get", "Get type details", new[] { "<type>" }) },
                    new[] { "<action>", "<type>" },
                    new[] { "--session" },
                    true
                ),
                new CapabilityCommand(
                    "callers",
                    "Get callers of a method",
                    "ecp callers <method> --session <id>",
                    Array.Empty<CapabilitySubcommand>(),
                    new[] { "<method>" },
                    new[] { "--session" },
                    true
                ),
                new CapabilityCommand(
                    "callees",
                    "Get callees of a method",
                    "ecp callees <method> --session <id>",
                    Array.Empty<CapabilitySubcommand>(),
                    new[] { "<method>" },
                    new[] { "--session" },
                    true
                ),
                new CapabilityCommand(
                    "references",
                    "Get references to a symbol",
                    "ecp references <symbol> --session <id>",
                    Array.Empty<CapabilitySubcommand>(),
                    new[] { "<symbol>" },
                    new[] { "--session" },
                    true
                ),
                new CapabilityCommand(
                    "read",
                    "Read file, method, or type source",
                    "ecp read <file|method|type> <target> --session <id> [--range start:end|--full]",
                    new[]
                    {
                        new CapabilitySubcommand("file", "Read file contents", new[] { "<file>" }),
                        new CapabilitySubcommand("method", "Read method source", new[] { "<method>" }),
                        new CapabilitySubcommand("type", "Read type source", new[] { "<type>" })
                    },
                    new[] { "<kind>", "<target>" },
                    new[] { "--session", "--range", "--full", "--id", "--query" },
                    true
                ),
                new CapabilityCommand(
                    "report",
                    "Generate a session report",
                    "ecp report --session <id>",
                    Array.Empty<CapabilitySubcommand>(),
                    Array.Empty<string>(),
                    new[] { "--session" },
                    true
                ),
                new CapabilityCommand(
                    "capabilities",
                    "List all available commands and options in JSON",
                    "ecp capabilities [--format json]",
                    Array.Empty<CapabilitySubcommand>(),
                    Array.Empty<string>(),
                    new[] { "--format" },
                    true
                )
            }
        );
    }
}

