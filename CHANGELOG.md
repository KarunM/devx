# Changelog
## 1.8.2 — 2026-10-14

### Fixed

- Refresh indexed symbol locations after incremental updates so snapshots report current line numbers.
- Route explicit Python test targets to pytest or the Python interpreter, including standalone scripts and mixed target lists.


## Unreleased

## 1.8.1 — 2026-10-14

### Fixed

- Index commands ignore values supplied to `--agent`, `--task`, `--session` and `--format` when resolving paths.
- Missing indexed files are removed and their dirty markers cleared; index status reports remaining dirty files.
- `index changed` outside Git returns a structured error with the `devx index <root>` fallback.
- A read-only `.devx/devx.db` no longer crashes `session create` or sessionless reads: unchanged repository rows are not rewritten, skipped history writes are reported as `HistoryNotRecorded`, and `session create` returns the existing automatic session with that warning.


## 1.8.0 — 2026-10-13

### Changed

- Internal restructuring: the entry point is split into per-command files, the storage and protocol engine are split into partial classes by concern, test names describe behaviour, and internal notes are removed from comments. No behaviour change; outputs are byte-identical to 1.7.2 on the captured query set.

## 1.7.2 — 2026-10-12

### Fixed

- Filter Python text-backed callers and references inside strings and comments. Module, class and function docstrings,
  multi-line string literals, f/raw/byte strings and `#` comments no longer produce hits (for example the `<module>`
  group for a name that only appears in a module docstring); the agent footer and the callers note now call the
  results lexical candidates, not a resolved call graph.

## 1.7.1 — 2026-10-12

### Fixed

- C# `callers`, `callees` and `references` resolve their target through the same resolver as `read` (exact, module,
  suffix, bare name), so `callers "RipgrepLocator.Locate"` answers instead of "was not found".
- C# `callers`/`callees` on a method with several overloads in one type answer for all of them, grouped per overload
  (`Overloads[]` with `Signature`, `Path`, `StartLine` and the callers/callees in JSON; one `<QN>(<parameter types>) path:line`
  header per overload in agent format) instead of failing with "matched multiple implementations".
- A name shared by methods of different types answers with the same capped candidate list as `read` (exit 2 for `callers`).
- Callers are attributed to an overload by argument count from the call site in each method body; a caller that is another
  overload of the same method is reported as `self-delegation`. Single-method answers are unchanged and gain `TargetSignature`.

## 1.7.0 — 2026-10-12

- `devx read type` outline gains one `bases:` line after the declaration: each declared base of a Python or
  C# type with `path:start-end` when it resolves to exactly one indexed type, `-` otherwise
  (`Metadata.Bases` in JSON), so a hierarchy is followed one `read type` per level (OSS26 B-1).
- Text-backed `callers` (Python) with more than 10 call sites groups them by enclosing function, busiest first
  (top 10 shown, all in JSON `Groups`); `--full` (MCP `full: true`) lists every site. `references` and
  C# `callers` are unchanged (OSS26 B-2).
- `devx references` and `devx callers` now work for Python symbols, text-backed: a whole-word search for
  the name over indexed files, each hit attributed to the enclosing indexed symbol and classified call,
  usage or import (50 hits max, calls first; `Mode: "text"` in JSON). `callees` still reports Python as
  unsupported, now with a hint to read the body. C# graph output is unchanged (OSS25 P-1, P-2).
- Content search no longer reports files that ripgrep reads but the index never covered as "indexed
  path(s) could not be searched safely"; they are counted in `Coverage.UnindexedHits` (OSS25 P-3).
- Fix ripgrep content search sitting out its 30 s timeout and returning nothing for a very common name
  (more than 5,000 matches): ripgrep is now stopped as soon as the scan ceiling is reached.
- Guidance: `read type` is stated to answer "which methods/fields does X have" (use `--full` for bodies),
  and `callers`/`references` are stated to work for Python while `callees` does not (OSS25 P-4).
- Shipped agent guidance (text only): the `devx init` rules block, `devx --help`, the `read`/`search`
  usage lines and the MCP `search`/`read`/`callers`/`callees`/`references`/`snapshot` descriptions now
  say that bare names resolve when unique, that `read type` is an outline with bases, and that
  callers/callees/references answer cross-reference questions before a text search (OSS24).
- `read type` (and bare `read <Class>` / MCP `read` kind=type) now returns an outline by default:
  the class signature with its bases and one line per direct member with its span; `--full` /
  `full:true` returns the whole body as before. The MCP `read` description and CLI usage now say
  that target is a qualified name, not a path, and point to the outline (O-1, O-2).
- History is best-effort: when the session database is read-only (for example a sandboxed agent),
  searches and reads succeed and report a "history not recorded" diagnostic instead of crashing
  with a SQLite error. Session creation and source/index writes still fail loudly (O-3).
- Read class bodies for bare and explicit type reads, resolve unique segment suffixes, cap and
  rank ambiguous candidates, and report truncation only when the requested source span is clipped.
- Fix an absolute in-root path in `devx patch apply` / `rollback` leaving a root-prefixed dirty marker
  that never matched the indexed file: dirty paths are now stored root-relative, so a refresh or full
  index clears them and no stale symbol survives.
- Fix `devx search text` hanging with ripgrep when stdin is a pipe (as under MCP): ripgrep now gets an
  explicit `.` search root and a closed stdin, and `./` is stripped from hit paths so they still match
  the index.
- Fix the MCP `test` tool ignoring `testFiles`: they are now forwarded as `targets`, so Python test
  files select pytest exactly as `devx verify test <file>` does.
- `src/DevX.Cli/Program.cs.bak` was removed from the repository.

### Known limitations

- C# `callers` can over-report: every method of the file was returned as a caller of a private helper on DevX's own source (pre-existing, not introduced in 1.7.0).
- Python text-backed `callers`/`references` match by name, so doctest `>>>` lines and names inside strings count as call sites.

## 1.6.14

A release about closing the loop after an edit. In 1.6.13 an agent that changed a file had to make a
second and third call to find out whether it had broken anything -- `devx replace`, then `devx build`,
then `devx test` -- and each of those answered with its own log path and its own shape. There is now
one command that does the whole check and answers in one compact summary, and the edit commands can
run it on request. Verifying stays **opt-in**: an edit that does not ask is byte-for-byte the call it
was in 1.6.13.

- **`devx verify [build|test|both] [<file|dir> ...]`** runs the build and, if it passes and the mode
  includes tests, the relevant tests, and returns one summary: build status and the first errors as
  `path:line: message`, then the test counts, the failing names with trimmed assertions, and the path
  of the full log. A bare `devx verify` is `both`; `devx verify build` is build-only, for the case
  where a test run is known slow. It honours the build and test commands from `.devx/config.json`
  (1.6.10) exactly as `devx build` and `devx test` do, and needs no `--session`. A successful run
  answers in ONE line -- status, counts, seconds, log path -- because a check that costs five lines of
  reply is a check an agent learns to skip.
- **Every edit command takes `--verify[=build|test|both]`.** `devx replace ... --verify` and
  `devx patch apply ... --verify` do the edit and the check in the same response: the message, the
  diff, and one verification line. A bare `--verify` is `both`; `--verify=build` stops after the
  build; `--no-verify` cancels it and the last flag on the line wins. **On a build failure the tests
  are skipped, the edit is kept, and the errors are returned** -- with `tests: skipped (build failed)`
  as the explicit reason, so nothing has to be inferred from an absent section.
- **MCP: a `verify` tool with the same modes**, for a client that checks without editing. The
  `edit.replace` tool's `verify` argument still accepts `true`, but it now also accepts a mode string
  (`"build"`, `"test"`, `"both"`).
- **Behaviour change: MCP `edit.replace` no longer verifies by default.** In 1.6.13 it ran the
  verification unconditionally; a client that wants it must now pass `verify`. This is intentional
  and matches the CLI, where verifying is opt-in -- an edit that silently spent a build and a test
  run is exactly the overhead this release was told to avoid. CLI `devx replace` without `--verify`
  is unchanged.
- **Agent guidance says the loop is one call.** The block `devx init` writes and `devx --help --agent`
  now read "make your edits, add `--verify` to the LAST one (or run `devx verify` once)", so a batch
  of edits costs one check rather than one check each, and `devx verify build` is named for a
  build-only check.
- **A support module under a test directory is no longer a test file.** `tests/conftest.py` and
  `tests/__init__.py` were classified as test files, and pytest collects nothing from them: it exits
  5, and a verify over five passing test files plus one `conftest.py` was reported as `FAIL tests —
  0 failed of 0`. The name-based rules now come first and those two names are excluded before the
  directory rule, so a verify on a Python repository reports what actually ran.
- **The capabilities note is corrected** (it claimed `report` needed no session) and gains the
  `verify` command and descriptor. `devx capabilities --format json` is otherwise unchanged: the
  diff against 1.6.13 is purely additive -- one added command, one added descriptor, no removed or
  altered command, and no changed key outside `CapabilityDescriptors` and `CompatibilityNotes`.

Cost, measured with `tools/verify_cost.py` on this repository (.NET) and on a click checkout
(pytest), wall-clock and response bytes:

| call | devx-oss | click |
| --- | --- | --- |
| `replace` | 0.36 s / 204 B | 1.89 s / 150 B |
| `replace --verify` | 16.11 s / 321 B | 4.29 s / 268 B |
| `replace --verify=build` | 1.31 s / 296 B | 0.69 s / 242 B |
| `verify` | 18.52 s / 132 B | 1.41 s / 131 B |
| `verify build` | 1.18 s / 94 B | 0.30 s / 94 B |

An edit that does not verify pays nothing for this: the ~100-130 bytes are the one-line answer, and
the seconds are the build and the tests themselves, which only run when asked for.

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
