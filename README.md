# DevX

DevX is a local code-intelligence and engineering-loop CLI. It indexes repositories, returns narrowly scoped source context, applies structured patches, and runs build/test validation.


> **History:** developed privately from June 2026 (Azure DevOps) and moved to GitHub on 7 October 2026 with its original commit history.

## Install or update

From the repository root:

```bash
dotnet tool uninstall --global devx.cli 2>/dev/null || true
dotnet pack src/DevX.Cli -c Release -o ./artifacts/tools
dotnet tool install --global --add-source ./artifacts/tools DevX.Cli
devx --version
```

The package is `DevX.Cli` and the installed command is `devx`. The repo-local `./devx` wrapper runs the current source directly.

DevX stores workspace state in `.devx/devx.db`.

Install or refresh agent instructions in the current repository with `devx init --agents claude,codex`. Add `--global` to target `~/.claude/CLAUDE.md` and `~/.codex/AGENTS.md`. The command owns only its marked DevX block, so rerunning it updates the rules without replacing other instructions.

## Core workflow

```bash
devx index .
SESSION_ID=$(devx session create --format json | jq -r '.Data.sessionId')
devx search text "needle" --session "$SESSION_ID" --agent orchestrator
devx read section 'docs/plan.md#Implementation' --session "$SESSION_ID" --agent worker-1
devx test tests/unit_test.py tests/beta_gate_check.py --session "$SESSION_ID" --timeout 120 --agent worker-1
devx report --session "$SESSION_ID" --format json
```

Symbol search results include the defining file and line range. Symbol reads accept IDs, short names, or qualified names; plain output prints `path:line` followed by unescaped source. Missing and ambiguous reads return the attempted name, a failure reason, and up to five suggestions. `search text` searches indexed file contents and returns file, line, column, and preview data.

`devx test <files>` detects pytest files and plain Python check scripts, runs them sequentially with a five-minute default timeout, and prints a compact result. Failures include `file:line`, a one-line message, and a full-log path. Test summaries, per-agent calls, response character counts, and estimated output tokens are recorded in `devx report`.

`devx index update` accepts multiple files in one call. JSON and HTML files are indexed as documents, and Git worktrees, where `.git` is a pointer file, are treated as healthy repositories.

## Build and test

Export a self-contained local HTML report across workspace sessions:

```bash
devx report html --period 30d --output devx-report.html
devx report html --session "$SESSION_ID" --format json --output devx-report.json
```

Organization aggregation is a preview for the Enterprise reporting capability. Supply a normalized event file (`AnalyticsInput`: `Organization` and `Events`) with globally unique event IDs and explicit repository, team, developer, agent, and session attribution:

```bash
devx report html --input org-events.json --org acme --team payments --repo backend --output devx-org-report.html
```

HTML and JSON share the same aggregation model. Reports compare the selected period with the preceding equal-length period, count validation runs, show measured output characters and labeled token estimates, and make evidence-based recommendations. Missing policy, audit, merge-quality, and savings data is shown as unavailable. Organization preview input is not authenticated; enterprise RBAC, subscription enforcement, and central collection still require implementation. Existing output files are never overwritten. Local reports include session-attributed events for the current repository; unscoped operations are excluded.

```bash
dotnet build DevX.sln
dotnet test DevX.sln
```

## Uninstall

```bash
dotnet tool uninstall --global DevX.Cli
```

## Notes

- Workspace-scoped commands evaluate the current working directory.
- `build`, `test`, and `report` require `--session <id>`.
- The MCP reference server is currently an integration-tested library surface, not a packaged long-running server command.
