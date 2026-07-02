# TASK-013: End-to-End Engineering Loop

- **Status:** Ready
- **Plan:** Full MVP
- **Depends On:** TASK-012

## Objective

Validate the full ECP workflow from repository discovery through patching and validation.

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
4. Execute `GET_FILE_SNAPSHOT`.
5. Execute `GET_METHOD_SUMMARY`.
6. Execute `GET_METHOD`.
7. Apply a known structured patch.
8. Run build.
9. Run tests.
10. Verify diagnostics are captured.
11. Verify session can be resumed.

## Acceptance Criteria

- Full workflow succeeds without sending the full repository to the LLM.
- Every context expansion is explicit.
- Patch application is structured.
- Build/test feedback is captured.
- Session state is persisted.
- The workflow can be repeated deterministically.

## Verification Steps

```bash
dotnet test
dotnet run --project src/Ecp.Cli -- index ./fixtures/sample
dotnet run --project src/Ecp.Cli -- search Example
dotnet run --project src/Ecp.Cli -- snapshot ./fixtures/sample/Example.cs
dotnet run --project src/Ecp.Cli -- build
dotnet run --project src/Ecp.Cli -- test
```

## Out of Scope

- Real LLM provider integration
- Multi-language support
- Embeddings
- Remote repositories

## Definition of Done

The MVP proves the core ECP loop:

```text
Search → Snapshot → Expand → Patch → Build → Test → Repair
```

using structured protocol operations and CLI-owned session state.
