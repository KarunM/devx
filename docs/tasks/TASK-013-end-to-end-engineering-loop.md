# TASK-013: End-to-End Engineering Loop

- **Status:** Completed
- **Completed:** 2026-07-02
- **Implemented By:** Codex
- **Verification:**
  - `dotnet test`
- **Plan:** Full MVP
- **Depends On:** TASK-012

## Objective

Validate the full DevX workflow from repository discovery through patching and validation.

## Prerequisites

All previous MVP tasks are complete.

## Inputs

- `docs/vision.md`
- `docs/specs/SPEC-001-context-protocol.md`
- `docs/specs/SPEC-002-repository-index.md`
- `docs/specs/SPEC-003-session-manager.md`
- `docs/specs/SPEC-004-patch-model.md`
- `docs/specs/SPEC-005-cli-specification.md`

## Required Workflow

```text
Index repository
Search symbol
Get file snapshot
Get method summary
Expand method source
Apply structured patch
Run build
Capture diagnostics
Run tests
Persist session
```

## Implementation Steps

1. Create an end-to-end fixture repository.
2. Index the fixture repository.
3. Execute `SEARCH_SYMBOL`.
4. Apply a known structured patch.
5. Run build.
6. Run diagnostics.
7. Run tests.
8. Verify session can be resumed.

## Acceptance Criteria

- Full workflow succeeds without sending the full repository to the LLM.
- Patch application is structured.
- Build/test feedback is captured.
- Session state is persisted.
- The workflow can be repeated deterministically.

## Verification Steps

```bash
dotnet test
dotnet test
```

## Out of Scope

- Real LLM provider integration
- Multi-language support
- Embeddings
- Remote repositories

## Definition of Done

The MVP proves the core DevX loop:

```text
Search → Snapshot → Expand → Patch → Build → Test → Repair
```

using structured protocol operations and CLI-owned session state.
