# Changelog

## 1.5.0 — 2026-10-07

- Add report failure codes, most-failed names, repeated session failures, and representative diagnostics, including historical failures whose reason was not recorded.
- Record read-time whole-file/returned character baselines and correlated read IDs. Report estimated tokens avoided versus whole-file reads, excluding and counting calls without baselines; never claim billing savings.
- Add optional `fallback observe` shell/harness telemetry, conservative bypass-candidate/allowed/unclassified categories, and explicitly reported completed monitoring spans. Coverage off is NOT COLLECTED, not compliance.
- Add `report export` and repeated `report html --input` aggregation, with workspace-namespaced event IDs and workspace rows.
- Add operation latency percentiles, daily activity, slowest calls, session/agent/task breakdowns, and `--task` attribution.
- Add index-health snapshots and an additive `file_index_observations` table for index timestamps; historical timestamps remain unknown until reindexing. Record unsupported incremental file skips.
- Preserve existing report JSON keys and HTML section titles. Add explicit collection-state metadata; authenticated identity, policy enforcement, merge outcomes and secret detection remain UNAVAILABLE.
- Candidate-only validation: no automatic global installation, deployment, or agent-hook activation.

## 1.4.1 — 2026-10-07

- `read method` returns definitions of indexed fields, classes, constructors, and Markdown sections as well as methods. Multiline assignments retain their full source spans.
- Resolve exact names first, then repository/ancestor prefixes, file module paths (with optional `<module>` and class containers), and finally short names. Supplied module paths constrain resolution; ambiguous results include every candidate's qualified name, kind, file, and lines.
- NotFound suggestions exclude the attempted qualified name. Out-of-workspace qualified names receive a workspace-specific error.
- Successful symbol reads include `ResolvedVia` metadata; failure metrics retain reason, attempted name, suggestions, and candidate details.
- Batch index updates split newline-separated arguments, continue past missing files, and return nonzero only when errors occur without any successful updates. Unknown text file types receive a warning and exit zero.
- Health and doctor report missing indexed files, listing the first ten. Symbol/file search results flag missing files, and reads return a structured file-missing error.
- Existing response fields and commands remain available. New metadata and error candidate fields are additive; no database schema changes.
