# SPEC-011 --- ECP-First Agent Workflow

## Goal

Make ECP the primary interface used by coding agents (Copilot, Codex,
Claude Code, Aider, Gemini CLI) for repository exploration and edits.

The agent should prefer `ecp` over direct file inspection whenever ECP
can satisfy the task.

## Workflow

1.  Verify the repository is indexed.

``` bash
ecp index status --format json
```

2.  Create or reuse a session.

``` bash
ecp session create --format json
```

3.  Discover code using ECP.

``` bash
ecp search symbol ...
ecp search text ...
```

4.  Read source using ECP.

``` bash
ecp read file ...
ecp read method --id ...
ecp read type --id ...
```

5.  Edit using structured patches.

``` bash
ecp patch preview ...
ecp patch validate ...
ecp patch apply ...
```

6.  Validate.

``` bash
ecp build
ecp test
ecp report
```

## Rules

-   Prefer ECP search/read APIs over opening source files.
-   Prefer structured patches over direct edits whenever ECP supports
    the change.
-   Use direct file edits only when the required operation cannot yet be
    represented by ECP.
-   Keep edits minimal and localized.
-   Validate with build/test before reporting completion.

## Success Criteria

-   Agent uses `ecp` as its primary interface.
-   Direct source reads are exceptions rather than the default.
-   Structured patch workflow is exercised for supported operations.

## Completion

-   **Completed:** 2026-07-02
-   **Implemented By:** Codex
-   **Verification:**
    - `ecp index status --format json`
    - `ecp session create --format json`
    - `ecp search symbol HandlePatchAsync --session <id> --format json`
    - `ecp read method --id 034dd08e-9fb5-b763-dd73-09346e813ccb --session <id> --format json`
    - `ecp read type --id a9a8465e-66ff-7713-fe64-8292f368180d --session <id> --format json`
    - `ecp build --session <id> --format json`
    - `ecp test --session <id> --format json`
    - `ecp report --session <id> --format json`
