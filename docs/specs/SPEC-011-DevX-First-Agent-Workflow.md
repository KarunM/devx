# SPEC-011 --- DevX-First Agent Workflow

## Goal

Make DevX the primary interface used by coding agents (Copilot, Codex,
Claude Code, Aider, Gemini CLI) for repository exploration and edits.

The agent should prefer `devx` over direct file inspection whenever DevX
can satisfy the task.

## Workflow

1.  Verify the repository is indexed.

``` bash
devx index status --format json
```

2.  Create or reuse a session.

``` bash
devx session create --format json
```

3.  Discover code using DevX.

``` bash
devx search symbol ...
devx search text ...
```

4.  Read source using DevX. Batch known symbol targets in one call instead of issuing one read per symbol.

``` bash
devx read file ...
devx read method --id ...
devx read type --id ...
devx read SymbolOne SymbolTwo SymbolThree --session <id>
```

5.  Edit using structured patches.

``` bash
devx patch preview ...
devx patch validate ...
devx patch apply ...
```

6.  Validate.

``` bash
devx build
devx test
devx report
```

Shipped guidance (the `devx init` rules block, `devx --help` and the MCP tool descriptions)
states three facts: (1) bare or `Class.member` names resolve when unique, so an agent that knows
a name reads it directly instead of searching first; `read type` returns the declaration with
bases plus a member outline (`--full` for the body) and a symbol hit already shows the declaration
line; (2) "who calls / what does it call / where is it used" is answered by `callers`, `callees`
and `references`, used before any text search; (3) `snapshot <file>` outlines a file and
`read file --range a:b` reads the lines around a hit.

## Rules

-   Prefer DevX search/read APIs over opening source files.
-   Prefer structured patches over direct edits whenever DevX supports
    the change.
-   Use direct file edits only when the required operation cannot yet be
    represented by DevX.
-   Keep edits minimal and localized.
-   Validate with build/test before reporting completion.

## Success Criteria

-   Agent uses `devx` as its primary interface.
-   Direct source reads are exceptions rather than the default.
-   Structured patch workflow is exercised for supported operations.

## Completion

-   **Completed:** 2026-07-02
-   **Implemented By:** Codex
-   **Verification:**
    - `devx index status --format json`
    - `devx session create --format json`
    - `devx search symbol HandlePatchAsync --session <id> --format json`
    - `devx read method --id 034dd08e-9fb5-b763-dd73-09346e813ccb --session <id> --format json`
    - `devx read type --id a9a8465e-66ff-7713-fe64-8292f368180d --session <id> --format json`
    - `devx build --session <id> --format json`
    - `devx test --session <id> --format json`
    - `devx report --session <id> --format json`
