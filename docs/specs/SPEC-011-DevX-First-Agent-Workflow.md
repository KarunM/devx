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

4.  Read source using DevX.

``` bash
devx read file ...
devx read method --id ...
devx read type --id ...
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
