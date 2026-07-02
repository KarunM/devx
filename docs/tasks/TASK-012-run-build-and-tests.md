# TASK-012: Build and Test Integration

- **Status:** Completed
- **Completed:** 2026-07-02
- **Implemented By:** Codex
- **Verification:**
  - `dotnet build`
  - `dotnet run --project src/Ecp.Cli -- session create --format json`
  - `dotnet run --project src/Ecp.Cli -- build --session <id> --format json`
  - `dotnet run --project src/Ecp.Cli -- test --session <id> --format json`
  - `dotnet run --project src/Ecp.Cli -- diagnostics --session <id> --format json`
- **Plan:** PLAN-005 Patch Engine
- **Depends On:** TASK-011

## Objective

Implement build and test execution with structured diagnostics capture.

## Prerequisites

- CLI exists
- Session store exists
- Patch engine exists

## Inputs

- `docs/specs/SPEC-001-context-protocol.md`
- `docs/specs/SPEC-003-session-manager.md`

## Scope

Implement protocol operations:

```text
RUN_BUILD
RUN_TESTS
GET_DIAGNOSTICS
```

## Implementation Steps

1. Create build execution service.
2. Execute `dotnet build`.
3. Capture stdout and stderr.
4. Parse compiler diagnostics where possible.
5. Execute `dotnet test`.
6. Capture test results.
7. Store diagnostics in session state.
8. Return structured diagnostics through protocol.

## Diagnostic Fields

Diagnostics should include where available:

- severity
- code
- message
- file
- line
- column
- raw output

## Acceptance Criteria

- Build can be executed from CLI.
- Tests can be executed from CLI.
- Diagnostics are captured.
- Diagnostics are associated with current session.
- Failed builds return structured errors, not only raw text.

## Verification Steps

```bash
dotnet run --project src/Ecp.Cli -- build
dotnet run --project src/Ecp.Cli -- test
dotnet run --project src/Ecp.Cli -- diagnostics --format json
```

## Out of Scope

- Non-.NET build systems
- CI integration
- Flaky test detection
- Test impact analysis

## Definition of Done

ECP can run builds and tests, capture diagnostics, and expose them through the protocol.
