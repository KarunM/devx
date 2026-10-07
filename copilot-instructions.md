# Copilot Instructions

Work this repository through the DevX workflow by default.

## HARD RULE (no exception)
- Use the DevX workflow completely. In the terminal, run ONLY `devx` commands (invoked via `dotnet run --project src/DevX.Cli -- <devx subcommand>`).
- Do NOT run any non-DevX terminal commands: no standalone `dotnet build`, `dotnet test`, `grep`, `sed`, `cat`, `nl`, `find`, etc.
- Use `devx build`, `devx test`, `devx search`, `devx read`, `devx snapshot`, `devx patch`, `devx ask`, and `devx report` for all repository inspection, validation, and workflow steps.
- Inspection MUST use DevX: `devx search`, `devx read`, `devx snapshot` — not direct file reads/greps, unless DevX cannot provide the information.
- Edits MUST go through DevX: `devx patch create/preview/validate/apply`. DevX is ALLOWED to directly modify repository files — `devx patch apply` writing files is the expected, sanctioned mechanism. Do NOT edit files with non-DevX tools.
- The ONLY non-`devx` terminal invocation permitted is `dotnet run --project src/DevX.Cli -- <subcommand>`, because that is how the DevX CLI itself is launched.
- The `devx ask` context/orchestration workflow is for testing the MLX model. Do NOT auto-apply the model's patch; validate whether it produces complete code blocks only.

- You are an DevX-native coding agent.
- Repository discovery MUST always use DevX.
- Never browse the repository directly unless DevX cannot provide the information.
- Reuse the current session. Never create a new session unless requested.
- Never re-index if the repository is already indexed.
- Prefer `search_symbol`, `search_text`, `snapshot`, `method summary`, `read_method`, and `read_type`.
- Only read full files when absolutely necessary.
- Minimize context retrieval and avoid duplicate searches.
- Never modify repository files with non-DevX tools. Modify files through DevX: generate structured DevX patches and apply them with `devx patch apply` (which directly writes the files).
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
- Use DevX commands for inspection, validation, and workflow steps. Non-DevX shell commands are prohibited.
- Keep changes focused and validate with the smallest relevant `devx` command after edits.
- When working on ask orchestration, preserve the structured protocol semantics and verify behavior with `devx session`, `devx ask`, and `devx report` against a real session.
