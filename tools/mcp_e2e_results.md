# `tools/mcp_e2e.py` — the same journey down both transports

Pinned checkouts from `/Users/karunamadu/repos/devx-bench/public`: Django
`djwork` (`1f0356f`) and sympy `sympywork` (`b4ce69a`). Each run drives the CLI and the
stdio MCP server with the same calls, so a row is a fair comparison rather than two
different scripts.

    python3 tools/mcp_e2e.py --bin <DevX.Cli> --repo <checkout> --label before --verbose

## The index step

This is the number that dominated every run, and the reason 1.6.9 exists. Both checkouts
were already indexed before each run, so a correct `index .` had nothing to do.

| checkout | 1.6.7 | 1.6.8 | 1.6.9 |
|---|---|---|---|
| djwork (3,289 files) | 800.8 s | 803.6 s | **0.2 s** |
| sympywork | 175.4 s | 169.4 s | **0.1 s** |

1.6.7 and 1.6.8 re-indexed everything. camp-92 measured the same thing from a second
probe: `Indexed 3292 files.` after 13 m 41 s, 90% CPU, 646 s of it system time. 1.6.9
answers `already current (3289 files).` instead; on a checkout with one edited file it
re-indexes that file in about a second.

## The journey

| row | djwork 1.6.7 | djwork 1.6.8 | djwork 1.6.9 | sympy 1.6.7 | sympy 1.6.8 | sympy 1.6.9 |
|---|---|---|---|---|---|---|
| search | 1/1 | 1/1 | 1/1 | 1/1 | 1/1 | 1/1 |
| read-roundtrip | — | — | — | 8/8 | 8/8 | 8/8 |
| read-range | — | — | — | 1/2 | **2/2** | **2/2** |
| read-section | — | — | — | 0/1 | 0/1 | 0/1 |
| edit.replace | — | — | — | 0/1 | 0/1 | 0/1 |
| test | — | — | — | 0/1 | 0/1 | 0/1 |
| **total** | 2 calls, 0 failed | 2 calls, 0 failed | 2 calls, 0 failed | 27 calls, 7 failed | 27 calls, **5 failed** | 27 calls, **5 failed** |

What changed between the arms, and what did not:

- **`read-range` 1/2 → 2/2.** One of the two range spellings (`595-615`) was rejected with
  `Read file requests must include either a 'range' field formatted as 'start:end' or --full`
  on 1.6.7; both spellings work from 1.6.8. This is item 2 of the brief, visible end to end.
- **The `test` error names the fix.** `No runtime target matched the requested runtime/project
  selection.` became `No runtime target to run: found python:., none of which matched the
  runtime/project selection. Declare what to run in .devx/config.json -- for example {...}`.
  Same failure, but the second one tells an agent what to do instead of dead-ending it.
- **`read-section` still fails, in all three.** `Heading 'Contributor Covenant Code of Conduct'
  was not found in 'CODE_OF_CONDUCT.md'`. Not a regression and not fixed by 1.6.8 -- worth a
  look, because the heading is in that file.
- **`cli test` still reports `FAIL tests — 0 failed of 0`.** pytest at the root of a sympy
  checkout collects nothing and calls it a pass. That is item 4's problem in its general form:
  sympy is neither Django nor a .NET project, so nothing auto-detects its runner, and without a
  `.devx/config.json` the honest answer is still a failure that reads like a pass.
- **`edit.replace` fails identically in both arms**, because the harness picks a string that
  occurs 33 times. That is the harness's choice, not a devx defect.

## Two honest limits of this harness

- **The Django arm only exercises `search`.** Its default queries find nothing in Django, so
  the journey aborts after two steps and 48 bytes of output. Only the index timing in the
  table above is meaningful for djwork; the journey rows are sympy's.
- **`test_target()` knows Django and sympy only.** On any other repository the `test` row
  reports a failure that is the harness talking, not devx.
- `--reuse-session` used to skip the index step as well as session creation, and the CLI
  transport then could not resolve an id the MCP transport accepted, so the CLI arm aborted
  after one call. The harness now always indexes -- which from 1.6.9 is 0.2 s on an up-to-date
  checkout, so there was no reason to skip it.
