# Reporting and optional shell observation

Reporting remains local and offline. Organization aggregation is a preview, not authenticated governance.

```sh
devx report html --period 30d --output report.html
devx report export --period 60d --output worktree-a.json
devx report html --input worktree-a.json --input worktree-b.json --output project.html
devx read method 'db.example' --session "$SESSION_ID" --agent deepseek --task brief-054
```

Export 60 days when comparing a current 30-day period to the preceding 30 days. Exports contain workspace-namespaced event IDs, optional history windows and current index-health snapshots. Repeated IDs are deduplicated. Sessionless calls are included in local exports; separate worktrees remain separate workspace rows even when repository names coincide. Output files are created exclusively: choose a new destination rather than overwriting an existing artifact.

Existing JSON fields remain unchanged for compatibility. `Reporting.Availability` and the new metric objects supply collection states. `ZERO` means collected with zero observations; `COLLECTED` accompanies a positive value; `NOT COLLECTED` means an existing source supplied no measurement for this period; `UNAVAILABLE` means no implemented source. The last two have null values and never display as zero. Existing numeric JSON counters are legacy fields; consumers needing coverage must use the additive states, not infer availability from those counters.

HTML and JSON reports accept `--anonymize` and `--blocked-terms <file>`. Anonymization replaces repository, organization, and workspace names with `repo-N`; file paths with `file-N`; symbol and attempted names with `symbol-N`; session and agent labels with `session-N`; and task labels with `task-N`. Labels are stable within a render and numbered by first appearance. Numeric values and the report structure stay unchanged. Free text that can contain arbitrary identifying content (including metadata, reasons, suggestions, commands, summaries, recommendations, and limitations) becomes `[redacted]`. To prevent a report from being written when it contains a known term, provide a blocked-terms file outside the repository with one term per line; blank lines and `#` comments are ignored. Matching is case-insensitive. A match exits with code 3 and prints only the aggregate match count; a missing or unreadable file exits with code 2.

Failure analysis retains legacy failures as `reason not recorded`. Savings use `(whole-file characters - returned characters) / 4`, clamped at zero, and exclude calls without a recorded baseline. These are estimated tokens avoided versus a whole-file-read baseline, not billing or demonstrated agent behavior. Protocol/CLI reads carry a shared ID, so the new savings and call-duration views count a correlated pair once. Existing event totals still include both rows for compatibility. Historical uncorrelated rows cannot be reliably collapsed.

Latency uses nearest-rank p50/p95 and UTC daily buckets; it does not measure developer time saved. Agent and task labels are caller supplied. Authenticated identity, policy enforcement, merge outcomes and secret detection stay UNAVAILABLE.

Index health is an on-demand snapshot. Missing files are counted and the first ten paths listed. Staleness compares mtime to the recorded indexing timestamp. Pre-upgrade files have no timestamp until reindexed; unknown or partial coverage is explicit. Unsupported-file skip counts cover explicit incremental updates, not every ignored/generated file in a full repository scan.

## Optional harness integration

`fallback observe` records an observation without executing the shell command:

```sh
devx fallback observe --session "$SESSION_ID" --agent deepseek --command 'rg people db.py'
devx fallback observe --session "$SESSION_ID" --agent deepseek --command 'cat logs/server.log'
devx fallback observe --session "$SESSION_ID" --agent deepseek --command 'cat db.py | head'
```

The classifications are `bypass candidate` for indexed source/docs in this repository, `allowed` for logs/build/generated/non-indexed files, and `unclassified` for piped or unknown scope. A candidate is not a confirmed policy violation. Complex shell syntax is intentionally not interpreted. A harness can provide repeated `--file <path>` operands for simple commands when it knows their scope.

An example Claude Code PreToolUse adapter is [tools/devx-pretooluse.py.example](../tools/devx-pretooluse.py.example). It runs with Python despite the example suffix; the suffix avoids adding a Python runtime target to a .NET repository. Opt in through your own harness configuration; DevX does not install or activate it. Supply `DEVX_ROOT`, `DEVX_SESSION`, and the brief's `DEVX_AGENT` label. For a Bash PreToolUse hook, invoke:

```sh
python3 /absolute/path/to/devx/tools/devx-pretooluse.py.example
```

The adapter requires DevX 1.5.0. `DEVX_EXECUTABLE` can point to an isolated candidate CLI for testing without replacing the global tool. It records tool requests, not proof that the command completed. The adapter writes observations only. The harness must separately report completed intervals during which it knows the hook was enabled for that session/agent:

```sh
devx fallback observe --session "$SESSION_ID" --agent deepseek \
  --coverage-start 2026-10-07T10:00:00Z --coverage-end 2026-10-07T10:15:00Z
```

Do not claim a future interval or an entire session just because one callback fired. Reports clip supplied spans to the reporting period. No observations with coverage off is NOT COLLECTED, never compliance. Observation events without intervals remain visible but have no continuous-coverage claim. Telemetry and completed spans are self-reported, not tamper-proof audit data; shell aliases and tools outside the observed harness remain invisible. Hook failures are non-blocking and print NOT COLLECTED to stderr.

Shell command text, attempted names and failure reasons may contain sensitive content. Observation is opt-in; review exports before sharing and do not pass secrets in observed commands. No telemetry is uploaded.

## Fixture sample

[Sample HTML](samples/reporting-sample.html), [fixture database](samples/reporting-fixture.db) and [normalized export](samples/reporting-events.json) are generated by the CLI regression fixture, not production data. The fixture's temporary source workspace is removed after testing; use the normalized export to reproduce its historical snapshot:

```sh
devx report html --input docs/samples/reporting-events.json --output reproduced.html --period 30d
```

The fixture shows two repeated NotFound failures, one legacy failure without a reason, a 40,000-character file read returning 2,000 characters (9,500 estimated tokens avoided), one observed bypass candidate, explicit short coverage and one unsupported `.txt` skip.
