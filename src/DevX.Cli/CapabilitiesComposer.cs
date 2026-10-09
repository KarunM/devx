using DevX.Application;

namespace DevX.Cli;

public sealed record CapabilityOption(string Name, string Description, string? Default, bool Required);
public sealed record CapabilitySubcommand(string Name, string Description, string[] Args);
public sealed record CapabilityCommand(string Name, string Description, string Usage, CapabilitySubcommand[] Subcommands, string[] Args, string[] Options, bool SupportsJsonOutput);
public sealed record CapabilitiesResponse(
    string Version,
    CapabilityOption[] GlobalOptions,
    CapabilityCommand[] Commands,
    string RuntimeKind,
    string RuntimeVersion,
    RuntimeContext? DefaultContext,
    RuntimeTargetDescriptor[] RuntimeTargets,
    RuntimeCapabilityDescriptor[] CapabilityDescriptors,
    string[] CompatibilityNotes);

public static class CapabilitiesComposer
{
    public static CapabilitiesResponse Compose(RuntimeHealthRecord? health = null)
    {
        var defaultContext = health?.Context;
        var runtimeTargets = BuildRuntimeTargets(health);
        var capabilityDescriptors = BuildCapabilityDescriptors(health);

        return new CapabilitiesResponse(
            "1.0",
            new[]
            {
                new CapabilityOption("--format", "Output format (json or text)", "text", false),
                new CapabilityOption("--session", "Session identifier for session-scoped commands", null, false),
                new CapabilityOption("--agent", "Agent label recorded with the operation", null, false)
            },
            new[]
            {
                new CapabilityCommand(
                    "version",
                    "Show the installed DevX CLI version",
                    "devx version [--format json] | devx --version",
                    Array.Empty<CapabilitySubcommand>(),
                    Array.Empty<string>(),
                    new[] { "--format" },
                    true
                ),
                new CapabilityCommand(
                    "init",
                    "Install or update marked DevX workflow rules for coding agents",
                    "devx init [--agents claude,codex] [--global] [--format json]",
                    Array.Empty<CapabilitySubcommand>(),
                    Array.Empty<string>(),
                    new[] { "--agents", "--global", "--format" },
                    true
                ),
                new CapabilityCommand(
                    "health",
                    "Report runtime readiness for the current workspace",
                    "devx health [--session <id>] [--format json]",
                    Array.Empty<CapabilitySubcommand>(),
                    Array.Empty<string>(),
                    new[] { "--session" },
                    true
                ),
                new CapabilityCommand(
                    "doctor",
                    "Provide deeper runtime diagnostics and remediation guidance",
                    "devx doctor [--session <id>] [--format json]",
                    Array.Empty<CapabilitySubcommand>(),
                    Array.Empty<string>(),
                    new[] { "--session" },
                    true
                ),
                new CapabilityCommand(
                    "ask",
                    "Ask the LLM to orchestrate code changes via tool calls",
                    "devx ask <prompt> --session <id>",
                    Array.Empty<CapabilitySubcommand>(),
                    new[] { "<prompt>" },
                    new[] { "--session" },
                    true
                ),
                new CapabilityCommand(
                    "index",
                    "Index a repository for discovery and search",
                    "devx index <path> | devx index status [--session <id>] | devx index update <file> [<file> ...] --session <id> | devx index changed --session <id>",
                    new[]
                    {
                        new CapabilitySubcommand("status", "Check index status for a repository", new[] { "--session" }),
                        new CapabilitySubcommand("update", "Re-index one or more files after changes", new[] { "<file>" }),
                        new CapabilitySubcommand("changed", "Re-index files currently changed in git status", new[] { "--session" })
                    },
                    new[] { "<path>" },
                    Array.Empty<string>(),
                    true
                ),
                new CapabilityCommand(
                    "search",
                    "Search indexed symbols or full text",
                    "devx search <symbol|text> <query> --session <id>",
                    Array.Empty<CapabilitySubcommand>(),
                    new[] { "<kind>", "<query>" },
                    new[] { "--session" },
                    true
                ),
                new CapabilityCommand(
                    "session",
                    "Session management",
                    "devx session create",
                    new[] { new CapabilitySubcommand("create", "Create a new session for the current repository", Array.Empty<string>()) },
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    true
                ),
                new CapabilityCommand(
                    "artifact",
                    "Create or manage reusable patch payload artifacts",
                    "devx artifact <create|delete|path> <name> [options]",
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
                    "devx patch <create|apply|preview|validate|rollback> <patch-file> --session <id>",
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
                    "devx build --session <id> [--runtime <kind>] [--target <path|name>]",
                    Array.Empty<CapabilitySubcommand>(),
                    Array.Empty<string>(),
                    new[] { "--session", "--runtime", "--target" },
                    true
                ),
                new CapabilityCommand(
                    "test",
                    "Run tests for the indexed solution",
                    "devx test [<file> ...] --session <id> [--runtime <kind>] [--target <path|name>] [--timeout <seconds>]",
                    Array.Empty<CapabilitySubcommand>(),
                    new[] { "<file>" },
                    new[] { "--session", "--runtime", "--target", "--timeout" },
                    true
                ),
                new CapabilityCommand(
                    "verify",
                    "Build and the relevant tests in one call",
                    "devx verify [build|test|both] [<file|dir> ...] [--runtime <kind>] [--target <path|name>] [--timeout <seconds>]",
                    Array.Empty<CapabilitySubcommand>(),
                    new[] { "<file>" },
                    new[] { "--session", "--runtime", "--target", "--timeout", "--filter" },
                    true
                ),
                new CapabilityCommand(
                    "diagnostics",
                    "Get diagnostics for the indexed solution",
                    "devx diagnostics --session <id>",
                    Array.Empty<CapabilitySubcommand>(),
                    Array.Empty<string>(),
                    new[] { "--session" },
                    true
                ),
                new CapabilityCommand(
                    "snapshot",
                    "Get a structural snapshot of a file",
                    "devx snapshot <file> --session <id>",
                    Array.Empty<CapabilitySubcommand>(),
                    new[] { "<file>" },
                    new[] { "--session" },
                    true
                ),
                new CapabilityCommand(
                    "method",
                    "Get method summary or full details",
                    "devx method <summary|get> <method> --session <id>",
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
                    "devx type get <type> --session <id>",
                    new[] { new CapabilitySubcommand("get", "Get type details", new[] { "<type>" }) },
                    new[] { "<action>", "<type>" },
                    new[] { "--session" },
                    true
                ),
                new CapabilityCommand(
                    "callers",
                    "Get callers of a method",
                    "devx callers <method> --session <id>",
                    Array.Empty<CapabilitySubcommand>(),
                    new[] { "<method>" },
                    new[] { "--session" },
                    true
                ),
                new CapabilityCommand(
                    "callees",
                    "Get callees of a method",
                    "devx callees <method> --session <id>",
                    Array.Empty<CapabilitySubcommand>(),
                    new[] { "<method>" },
                    new[] { "--session" },
                    true
                ),
                new CapabilityCommand(
                    "references",
                    "Get references to a symbol",
                    "devx references <symbol> --session <id>",
                    Array.Empty<CapabilitySubcommand>(),
                    new[] { "<symbol>" },
                    new[] { "--session" },
                    true
                ),
                new CapabilityCommand(
                    "read",
                    "Read file, method, or type source",
                    "devx read <file|method|type|section> <target> --session <id> [--range start:end|--full]",
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
                    "devx report --session <id> | devx report html [--period 30d] [--output <file>] [--input <events.json>]",
                    new[] { new CapabilitySubcommand("html", "Export a self-contained engineering report", Array.Empty<string>()) },
                    Array.Empty<string>(),
                    new[] { "--session", "--period", "--output", "--input", "--org", "--team", "--repo", "--format" },
                    true
                ),
                new CapabilityCommand(
                    "capabilities",
                    "List all available commands and options in JSON",
                    "devx capabilities [--format json]",
                    Array.Empty<CapabilitySubcommand>(),
                    Array.Empty<string>(),
                    new[] { "--format" },
                    true
                )
            },
            health?.Context.RuntimeFamily ?? "repository",
            "1.0",
            defaultContext,
            runtimeTargets,
            capabilityDescriptors,
            new[]
            {
                "Build, test, and verify resolve the repository's session themselves; report requires explicit --session selection.",
                "Human-readable CLI output remains the default operator experience.",
                "Machine-readable JSON is the stable compatibility surface for hosts and automation.",
                "Capability availability may be degraded or unavailable when repository prerequisites are missing."
            }
        );
    }

    private static RuntimeTargetDescriptor[] BuildRuntimeTargets(RuntimeHealthRecord? health)
    {
        if (health?.Context is null)
        {
            return [];
        }

        var basePrerequisites = new[]
        {
            new RuntimePrerequisite("workspace", health.Status == "Unavailable" ? "Missing" : "Satisfied", "Workspace root must exist."),
            new RuntimePrerequisite("index", health.Checks.Any(check => check.Name == "index" && check.Status == "Healthy") ? "Satisfied" : "Missing", "Repository index must be current."),
        };

        return
        [
            new RuntimeTargetDescriptor(
                RuntimeKind: health.Context.RuntimeFamily,
                Label: $"{health.Context.RuntimeFamily} target",
                ProjectPath: health.Context.ProjectPath,
                AvailabilityStatus: health.Status,
                Prerequisites: basePrerequisites)
        ];
    }

    private static RuntimeCapabilityDescriptor[] BuildCapabilityDescriptors(RuntimeHealthRecord? health)
    {
        var availability = health?.Status ?? "Degraded";
        var defaultPrerequisites = new[]
        {
            new RuntimePrerequisite("workspace", health is null ? "Unknown" : health.Checks.FirstOrDefault(check => check.Name == "workspace")?.Status ?? "Unknown", "Workspace root must exist."),
            new RuntimePrerequisite("index", health is null ? "Unknown" : health.Checks.FirstOrDefault(check => check.Name == "index")?.Status ?? "Unknown", "Repository index must be current for discovery/search flows.")
        };

        return
        [
            new RuntimeCapabilityDescriptor("search", "discovery", "1.0", "Search indexed symbols and file contents.", availability, defaultPrerequisites, ["cli", "protocol", "mcp"], ["devx search text <query> --session <id>"]),
            new RuntimeCapabilityDescriptor("read", "discovery", "1.0", "Read file, method, type, or Markdown section source with explicit scope.", availability, defaultPrerequisites, ["cli", "protocol", "mcp"], ["devx read section docs/plan.md#Heading --session <id>"]),
            new RuntimeCapabilityDescriptor("patch.create", "patch", "1.0", "Create a structured engineering patch document.", availability, defaultPrerequisites, ["cli", "protocol"], ["devx patch create <file> --session <id> --op ReplaceMethod --path src/File.cs"]),
            new RuntimeCapabilityDescriptor("patch.validate", "patch", "1.0", "Validate a structured engineering patch document.", availability, defaultPrerequisites, ["cli", "protocol", "mcp"], ["devx patch validate <file> --session <id>"]),
            new RuntimeCapabilityDescriptor("patch.apply", "patch", "1.0", "Apply a validated structured engineering patch document.", availability, defaultPrerequisites, ["cli", "protocol", "mcp"], ["devx patch apply <file> --session <id>"]),
            new RuntimeCapabilityDescriptor("patch.rollback", "patch", "1.0", "Rollback a previously applied structured engineering patch document.", availability, defaultPrerequisites, ["cli", "protocol"], ["devx patch rollback <file> --session <id>"]),
            new RuntimeCapabilityDescriptor("build", "validation", "1.0", "Build the selected runtime target.", availability, defaultPrerequisites, ["cli", "protocol"], ["devx build --session <id> --target src/App/App.csproj"]),
            new RuntimeCapabilityDescriptor("test", "validation", "1.0", "Run tests for the selected runtime target.", availability, defaultPrerequisites, ["cli", "protocol"], ["devx test --session <id> --target tests/App.Tests/App.Tests.csproj"]),
            new RuntimeCapabilityDescriptor("verify", "validation", "1.0", "Run the build and the relevant tests in one call.", availability, defaultPrerequisites, ["cli", "protocol"], ["devx verify", "devx verify build", "devx replace <file> --old <text> --new <text> --verify"]),
            new RuntimeCapabilityDescriptor("index.incremental", "indexing", "1.0", "Refresh repository index state incrementally.", availability, defaultPrerequisites, ["cli"], ["devx index update <file> [<file> ...] --session <id>", "devx index changed --session <id>"]),
            new RuntimeCapabilityDescriptor("diagnostics", "health", "1.0", "Inspect runtime readiness and captured diagnostics.", availability, defaultPrerequisites, ["cli", "protocol"], ["devx health --session <id>", "devx doctor --session <id>"]),
            new RuntimeCapabilityDescriptor("report", "reporting", "1.0", "Summarize session activity, validation, and fallback history by agent.", availability, defaultPrerequisites, ["cli", "mcp"], ["devx report --session <id>"])
        ];
    }
}
