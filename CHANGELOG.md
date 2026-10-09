# Changelog

## 1.6.13

A release about the call that came back an error, and about the engine that was usually missing. In the codex-1611
arm-B transcripts `devx read section` was the largest single source of failed calls -- 30 calls, 23 of them answered
with an error -- and 27 of those 30 were aimed at a `.py` or `.cs` file rather than a document. The other gap was
quiet: `devx search text` only used ripgrep when the machine happened to have `rg` on `PATH`, so on a stock machine
the slower in-process scan answered every content search.

- **`devx read section` accepts what the caller actually typed.** On a non-Markdown file, `devx read section
  <file>#<name>` resolves `<name>` as a symbol in that file and answers with exactly what `devx read method` gives,
  with the output naming the symbol it resolved to, so `read section alpha.py#load_level` is a hit rather than a
  `NotFound`. `<file>#` with no name answers with the file, capped and paged as `devx read <file>` does -- the CLI no
  longer rejects the trailing `#`. An ambiguous name lists the candidates as ready-to-run `devx read method "..."`
  commands and an unknown name lists the symbols the file does hold, so either way the next call is one the agent
  can paste. Markdown headings are unchanged, including the legacy name+path lookup, and `--format json` for
  `read section README.md#Usage` returns the same bytes it did in 1.6.12.
- **A heading that was indexed but could not be read back is read now.** The legacy heading lookup re-parses the file
  and can only find a method it recognises again, so a class, a Python function whose indexed type is `<module>`, a
  file whose row is gone or a file no longer on disk all ended in `ParseError`; the markdown path now falls back to
  the resolver, which reads the span straight from the index.
- **ripgrep ships with DevX.** The lookup order is `.devx/config.json` `"rg": {"path": ...}` (a repository that wants
  its own build; relative paths resolve against the root), then the copy shipped beside the binary or inside the tool
  package, then `rg` on `PATH`, then the in-process scan -- so `devx search text` is the fast engine that respects
  `.gitignore` on a machine that has nothing installed, and no call fails without it. The binaries are never
  committed: the pinned ripgrep 15.2.0 release is downloaded per runtime identifier by `tools/fetch_ripgrep.py` and
  checked against a recorded sha256 before it is unpacked, by the release workflow before packing and before
  archiving each single-file binary. `devx doctor` reports which engine is active, where it is and its version, in
  text and under `SearchEngine` in JSON. THIRD-PARTY-NOTICES carries ripgrep's Unlicense and MIT text and travels in
  the package and the archive. The tool package grows by ~9.5 MB for the five supported RIDs.

## 1.6.12

A release about not needing the second call. In the 1.6.11 arm-B transcripts the agent made 349 calls where arm A
made 153, and the difference was almost entirely `devx search text` (114 calls, 25 of them empty), `devx index
update` (43, pure overhead) and `devx read section` (30, 23 of them answered with an error). Each of those is a call
that should have been answered the first time, or should not have been needed at all.

- **`devx search text` is a regex search, and it answers in one call.** It takes full regex -- `Load(Level|Moniker)`
  matched nothing before and now matches both -- and it matches several queries in one call (`devx search text "a"
  "b"`), so an agent unsure of a name is not forced into a second round trip. ripgrep is the engine when `rg` is on
  `PATH` (fast, regex, respects `.gitignore`), with the in-process index scan as the fallback; the fallback takes a
  regex too, so the answer does not depend on whether ripgrep is installed. A pattern that will not compile --
  `GetValueOrDefault(`, which agents really typed -- is retried as a literal rather than reported as no matches,
  which was indistinguishable from "not in this repository".
- **`devx search text` results look like `rg -n` results, plus the symbol.** Each hit is
  `path:line: <the matching line>` followed by the enclosing symbol in brackets, so the next call is a `devx read`
  of a name the agent already has instead of another hunt. The cap still applies, and its message now says how to
  narrow rather than only that it truncated. `--format json` is unchanged: the symbol is added in the agent
  rendering only, and `ContentSearchResult` keeps its five fields.
- **Every write re-indexes the files it touched.** `devx replace`, `devx patch apply`, `devx patch rollback`, the
  patch create/delete operations and the edits made over MCP all leave the index describing the new text, so the
  `devx index update` after every edit that the harness used to demand -- 43 calls in one series of 39 runs -- is no
  longer needed. A file the write deleted is removed from the index rather than left behind as a document that is not
  there. `--no-index` opts out. The `devx init` rules no longer mention `index update` as a step after editing.
- **The generated rules teach the one-call forms.** `devx read <name> <name>` reads several symbols in one call, and
  a `devx search` that matches exactly one symbol already returns its source, so the rules now say not to spend a
  second call on what the first one answered.

Known: `devx read section` is unchanged in this release. Its 30 calls in the transcripts were 23 failures, and they
are mostly the caller's argument rather than the reader's fault -- 8 passed a bare `file#` with no heading at all
(the call they wanted was `devx read <file>`), 8 named something that is not a heading (`Option`,
`LevelOutputFormat`, a pytest function), 1 was an ambiguous prefix, and 3 were a heading the index holds but cannot
map back to a source span. Changing it is a design question, not a release-week patch.

## 1.6.11

A release about being findable. In the arm-B benchmark transcripts the agent lost up to 8 of 11 failures to the same
three things: it never saw `devx replace`, it invented patch op names and learned they were wrong a round trip later,
and it spent a call on `devx ask`, which failed against an endpoint nobody had configured.

- **`devx --help` prints the agent cheat sheet whatever global flags are present.** The help check only looked at
  `args[0]`, so the invocation the workspace rules produce -- `devx --session <id> --agent codex --task <label> --help`
  -- fell through to the dispatcher and answered `Unknown command '--session'`. `--help` is now recognised wherever it
  sits among the leading options, while the first real argument still wins: `devx ask "explain --help"` is a question,
  not a help request. The cheat sheet also names the edit tool it was missing -- `devx replace <file> --old <text>
  --new <text>` -- next to `devx patch`; on 1.6.10 it named only `patch`, which is how the op names came to be guessed.
- **`devx patch create` refuses an op it cannot apply, where the agent typed it.** An unknown op used to be written to
  the artifact and rejected by `patch validate` one round trip later. It now fails immediately with
  `unknown op 'replace_text'; for a text edit use: devx replace <file> --old <text> --new <text>`, the list of the eight
  ops the build accepts, and a `Did you mean 'ReplaceText'?` line for a near miss. Nothing is written. `TextReplace`,
  accepted since before 1.6.10, still works and is not advertised as its own op; no new aliases were added.
  `devx patch create --help` lists every op with its flags, which closes the 1.6.8 known issue about the op list being
  unobtainable without a failing call.
- **`devx ask` refuses instead of dialling an endpoint nobody configured.** With no `.devx/config.json` it fell back to
  `http://localhost:8080`: the agent paid for a call, waited on a connection and learned nothing. It now prints
  `devx ask is not configured (set llm.endpoint in .devx/config.json)` and exits 1 before reading stdin, touching the
  index or opening a socket -- measured at 0.05 s. The three hard-coded localhost fallbacks are gone, and `ask` is listed
  in the help only where a config exists.
- **`devx init --agents` teaches the edit step.** The generated rules described how to find and read code and never how
  to change it, which is the guidance gap behind the invented op names. They now carry `devx replace`, `devx patch
  create`, and the four steps spelled out: find it, read just it, change it, test it.

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

### Known issue, not fixed here

- **A target-selected `devx build` on a Python repository compiles only the first 200 `.py` files in ordinal-sorted
  path order, and reports PASS for the prefix.** `RunPythonBuildAsync` caps its `py_compile` sweep at 200 files, so on
  a large checkout the tail of the tree is never checked and a syntax error past the cap is invisible. The whole-tree
  `compileall` branch is only reached with a null selector, which no CLI caller supplies -- so a Python repository that
  consumes Django or sympy is "built" by a prefix and always says OK. Measured on Django `1f0356f`: 200 files, all
  under `django/`, PASS in 0.1 s. Declare an explicit `build.command` naming your package (for example
  `python -m compileall -q django`) to get a real build. Left unfixed at the request of the benchmark work: the
  behaviour is a cap, not a crash, and changing what `devx build` covers would move every pinned benchmark result.

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
