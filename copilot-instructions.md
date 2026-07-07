# Copilot Instructions

Work this repository through the ECP workflow by default.

## HARD RULE (no exception)
- Use the ECP workflow completely. In the terminal, run ONLY `ecp` commands (invoked via `dotnet run --project src/Ecp.Cli -- <ecp subcommand>`).
- Do NOT run any non-ECP terminal commands: no standalone `dotnet build`, `dotnet test`, `grep`, `sed`, `cat`, `nl`, `find`, etc.
- Use `ecp build`, `ecp test`, `ecp search`, `ecp read`, `ecp snapshot`, `ecp patch`, `ecp ask`, and `ecp report` for all repository inspection, validation, and workflow steps.
- Inspection MUST use ECP: `ecp search`, `ecp read`, `ecp snapshot` — not direct file reads/greps, unless ECP cannot provide the information.
- Edits MUST go through ECP: `ecp patch create/preview/validate/apply`. ECP is ALLOWED to directly modify repository files — `ecp patch apply` writing files is the expected, sanctioned mechanism. Do NOT edit files with non-ECP tools.
- The ONLY non-`ecp` terminal invocation permitted is `dotnet run --project src/Ecp.Cli -- <subcommand>`, because that is how the ECP CLI itself is launched.
- The `ecp ask` context/orchestration workflow is for testing the MLX model. Do NOT auto-apply the model's patch; validate whether it produces complete code blocks only.

- You are an ECP-native coding agent.
- Repository discovery MUST always use ECP.
- Never browse the repository directly unless ECP cannot provide the information.
- Reuse the current session. Never create a new session unless requested.
- Never re-index if the repository is already indexed.
- Prefer `search_symbol`, `search_text`, `snapshot`, `method summary`, `read_method`, and `read_type`.
- Only read full files when absolutely necessary.
- Minimize context retrieval and avoid duplicate searches.
- Never modify repository files with non-ECP tools. Modify files through ECP: generate structured ECP patches and apply them with `ecp patch apply` (which directly writes the files).
- Preferred workflow: search -> read only required methods -> create_patch -> patch_preview -> patch_validate -> patch_apply -> build -> test -> report.
- Never skip validation. Every code change must end with build, test, and report.
- If patch_apply fails, repair the patch and do not build.
- If build fails, fix the code and do not finish.
- If tests fail, fix the tests and do not finish.
- Never invent tool names.
- Never use write_file, create_file, replace_file unless those tools are explicitly advertised.
- Never return finished=true until patch_apply succeeded, build succeeded, tests succeeded, and report was generated, or a structured no-change decision has been accepted.
- Always reuse the current session.
- Never perform duplicate searches.
- When a tool is rejected, read the correction, adjust the plan, and continue without retrying the invalid tool.
- If budgets are exceeded, move to the next valid phase and do not repeat rejected operations.
- When reporting completion include files modified, files created, verification commands, session report, and remaining limitations.
- Use ECP commands for inspection, validation, and workflow steps. Non-ECP shell commands are prohibited.
- Keep changes focused and validate with the smallest relevant `ecp` command after edits.
- When working on ask orchestration, preserve the structured protocol semantics and verify behavior with `ecp session`, `ecp ask`, and `ecp report` against a real session.
