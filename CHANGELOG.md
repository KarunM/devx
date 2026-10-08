# Changelog

## 1.6.10

- **A configured test or build command now applies to every call, whatever the target.** `.devx/config.json` was
  consulted only when the caller named a runner *label*: a selector that named something on disk (`devx test
  tests/test_utils.py`, `devx test tests/DevX.Tests/DevX.Tests.csproj`) was treated as a path and skipped the config
  entirely, so the repository's own answer was ignored for most real calls. The config is now read first and wins for
  bare, path and label targets alike, with `{target}` substituted from whatever the caller named (empty when bare).
  Bare calls no longer have the discovered project path substituted into `{target}`, which on Django meant
  `runtests.py .` -- not a label.
- **A repository that declares a command can build and test even when discovery finds nothing.** `devx build` and
  `devx test` used to fail with "No runtime target to run" before reading the config, which left a documentation tree
  -- or any repository whose tests are driven by a script -- with no way to run through DevX at all.

## 1.6.9

Two bugs found by using 1.6.8 rather than by reading it.

- **`devx index .` no longer re-indexes a repository that is already indexed.** 1.6.8 gave the MCP `index` tool the
  "nothing changed, say so and stop" short-circuit but left the CLI command walking and re-parsing every file. On the
  Django benchmark checkout -- 3,292 files -- that is over 13 minutes of work to arrive at the index already on disk,
  and it read as a hang. The CLI now answers `already current (N files).` in under a second when git reports nothing
  changed. When a few files have changed it re-indexes only those files, instead of the whole tree; a repository that has
  never been indexed, or one where git cannot say what changed, still gets the full run. `devx index . --force` runs the
  full index regardless.
- **A configured build or test command that installs now runs.** An agent that has to restore a dependency before it can
  build anything is not doing something wrong, and refusing it only pushed the install outside any log DevX could see --
  which is what the Django agent did. `"blockInstall": true` in the same `.devx/config.json` block restores the 1.6.8
  refusal for a repository that wants it. DevX still never adds an install step of its own.
- Carried forward, unfixed: `devx patch create --help` prints only top-level usage, so per-op flags are undiscoverable
  (see 1.6.8 below).

## 1.6.8

The MCP server had to work end to end for an agent on a repository that is not a .NET solution. These are the failures a
Django agent hit while fixing one real bug: it made 7 useful calls and ~35 wasted ones, and finished by installing pytest
into the task venv to make the tool agree with it.

- **`search` and `read` agree on names.** A Python qualified name has a dot in its last segment, so `Path.HasExtension`
  read `db.models.query.QuerySet.count` as a file and `read` answered `file missing: ...`. A name is a file only when it
  looks like one, or when the caller asked for a file by passing `range`/`full`. A read that fails now carries the closest
  names that would work, on MCP as well as CLI -- and keeps saying when a name belongs to a different workspace.
- **Line-range reads.** MCP and CLI `read` accept `path` + `range` as `595-615` or `595:615`, for any file kind. A range
  reads a file whatever `kind` the caller declared. Long ranges are capped at 400 lines and page via `NextRange` rather
  than truncating. Sections keep `file#heading`.
- **Build and test that work on real repositories.** `.devx/config.json` may declare a `build` or `test` command (a
  `command` + `args`, or a whole command line such as `cd tests && ../.venv/bin/python runtests.py {target}`), and the
  repository's own answer wins over discovery. Django is auto-detected through `tests/runtests.py`, with or without a
  label, instead of letting pytest collect nothing and report it as a pass. A whole-repo Python build is one `compileall`
  with the vendored trees excluded, not thousands of paths on a command line that overflows `ARG_MAX`. DevX never installs
  packages: an install command is refused before it runs. "No runtime target matched the requested runtime/project
  selection" became a message that names what was found and the config key to set. A failed build or test carries the first
  `path:line: message` diagnostics instead of a bare "Build failed.".
- **No needless indexing.** The MCP server re-indexes the files the working tree changed when it starts, incrementally. A
  whole-repository `index` with nothing changed answers `already current (N files)` and does no work, and the `index` tool
  description says it is rarely needed.
- **One result shape.** Every MCP tool answers with the compact agent text on success *and* on failure; JSON is returned
  only for `format: json`. Previously a one-line success from `search` could be followed by a several-thousand-byte
  `{"error": ...}` envelope from the next tool.

### Known issues

- `devx patch create --help` prints only the top-level usage, so the flags for each `--op` are undiscoverable without a
  failed apply. `ReplaceLineRange` wants `--position "10:15"` and `--content`, and `--content` takes the replacement text
  literally -- passing a path writes the path into the source. `InsertMethod` wants `--method-text`. Two operations
  against the same file in one patch are rejected as a duplicate. None of this is fixed in 1.6.8.

## 1.6.7

The lean output is now the default, not a flag the caller has to know about.

- When stdout is not a terminal -- a pipe, a redirect, a benchmark harness -- the CLI now defaults to the agent format. Passing the flag was the whole gap: `devx search Async` returned 110,795 bytes in the old default against 926 in agent format, and the benchmark arm calls the CLI without `--format`, so it never saw the lean output
- A known agent marker in the environment (`CODEX_*`, `CLAUDE*`, `CURSOR_*`, `AIDER_*`, `AGENT`, `AI_AGENT`) also selects the agent format, for harnesses that allocate a pty and would otherwise look interactive
- An explicit `--format json`, `--format text` or `--format agent` always wins, byte-identical to 1.6.6
- `devx --help --agent` documents the rule

## 1.6.6

Fewer calls per task: search answers with less padding, a one-hit search also returns the source, and the errors that used to dead-end an agent now name the fix.

- Symbol and text searches in agent format are capped at 10 hits with a "... N more" line (explicit `--format json` is unchanged)
- A symbol search with exactly one hit appends that hit's source (capped at 80 lines; a longer method returns its signature and span plus "read to get N lines"). Several equal hits are always listed, never silently resolved to one. CLI and MCP
- A failed read returns the closest candidates as ready-to-use qualified names
- `devx read section <file>` without a `#Heading` lists the file's headings as paste-ready commands
- An unknown subcommand suggests the nearest one
- `devx --help --agent` prints a 10-line cheat sheet (the full manual is unchanged without `--agent`), and documents bare `devx read A B` for reading several symbols in one call

## 1.6.5

- `devx replace` one-shot edit (CLI) and `edit.replace` (MCP) with `--verify`: re-index, build and run relevant tests in one response
- No-session CLI: automatic per-repository session shared with the MCP server, automatic first-use indexing
- `devx read <name>` by bare symbol name, several targets per call; `devx search <query>` defaults to symbols with text fallback
- Compact agent output (`--format agent`, default for MCP; `format: json` opts out); compact build/test summaries with the full log in a file
- `devx test` runs standalone check scripts that print "N/M checks passed"
- Agent quick start in `devx --help` and in MCP tool descriptions

## 1.6.4

- `devx patch` supports text replacement (`ReplaceText`, alias `TextReplace`) with `--occurrence` and `--all`
- `devx test` accepts `--filter`, resolves directories inside .NET test projects, never labels .NET runs as pytest, always prints a result, and fails when a filter matches no tests

## 1.6.3

- C# types declared in the global namespace are now indexed

## 1.6.2

- Single-file builds no longer crash when indexing Python files
- Release workflow smoke-tests the published single-file binary on every target

## 1.6.1

- `read method` finds functions declared in top-level statements (for example `Program.cs`)
- `snapshot` accepts `--session`, `--agent` and `--task` before or after the file
- `test <file>` runs a single C# or Python test file with the matching runner

## 1.6.0 — open-source release

- Apache-2.0 licence
- CLA
- Community files
- Feature extension point
- MCP server (`devx mcp serve`)
- `devx report --anonymize` and `--blocked-terms`
- Indexing of plain folders without a project file
- CI and release workflow

## 1.5.0

See repository history for changes in this release.
