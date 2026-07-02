# TASK-008: Implement Session Store

- **Status:** Completed
- **Plan:** PLAN-004 Session Manager
- **Depends On:** TASK-007

## Objective

Implement persistent engineering sessions owned by the CLI.

## Prerequisites

- Storage project exists
- Protocol models exist
- Search API exists

## Inputs

- `docs/specs/SPEC-003-session-manager.md`
- `docs/adr/ADR-004-cli-owned-session-state.md`

## Scope

Persist:

- session metadata
- working set
- expanded files
- expanded symbols
- search history
- diagnostics history
- patch history
- build/test history references

## Implementation Steps

1. Define session domain models.
2. Add session tables or storage documents.
3. Implement session creation.
4. Implement session loading.
5. Implement working set update.
6. Implement context expansion tracking.
7. Add tests for session resume.

## Acceptance Criteria

- A session can be created.
- A session can be saved.
- A session can be resumed.
- Search history is retained.
- Expanded context is tracked.
- The LLM is not treated as the state owner.

## Verification Steps

```bash
dotnet test
```

## Out of Scope

- LLM adapter memory
- Conversation summarization
- Remote session sync

## Definition of Done

The CLI has a persistent session store capable of tracking the current engineering working set.

## Completion

- **Completed:** 2026-07-02
- **Implemented By:** Codex
- **Verification:**
  - `dotnet build`
  - `dotnet test`
