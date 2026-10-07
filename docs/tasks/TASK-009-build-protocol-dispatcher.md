# TASK-009: Build Protocol Dispatcher

- **Status:** Completed
- **Plan:** PLAN-003 Context Protocol Engine
- **Depends On:** TASK-008

## Objective

Implement the protocol dispatcher that receives structured DevX requests and routes them to handlers.

## Prerequisites

- Search API exists
- Session store exists
- Protocol request/response models exist

## Inputs

- `docs/specs/SPEC-001-context-protocol.md`
- `docs/plans/PLAN-003-context-protocol-engine.md`

## Scope

Support dispatch for MVP operations:

```text
SEARCH_SYMBOL
SEARCH_TEXT
GET_FILE_SNAPSHOT
GET_TYPE
GET_METHOD_SUMMARY
GET_METHOD
GET_CALLERS
GET_CALLEES
GET_REFERENCES
GET_TESTS
GET_DIAGNOSTICS
PATCH
RUN_BUILD
RUN_TESTS
```

Handlers may return `NotImplemented` where dependencies are not ready, but the dispatcher contract should exist.

## Implementation Steps

1. Define base protocol request type.
2. Define base protocol response type.
3. Define operation enum or discriminator.
4. Implement dispatcher interface.
5. Implement handler registration.
6. Implement search handlers.
7. Stub unsupported handlers with structured errors.
8. Add tests for routing and error handling.

## Error Handling

The dispatcher should return structured errors for:

- unknown operation
- invalid request
- missing session
- missing repository
- handler not implemented

## Acceptance Criteria

- Dispatcher routes known operations.
- Unknown operations return structured error.
- Search operations execute through dispatcher.
- Session state is updated where appropriate.
- Tests cover success and failure cases.

## Verification Steps

```bash
dotnet test
```

## Out of Scope

- LLM adapter
- CLI user interface
- Patch execution
- Build execution

## Definition of Done

The DevX protocol engine can receive structured requests and dispatch them predictably to operation handlers.

## Completion

- **Completed:** 2026-07-02
- **Implemented By:** Codex
- **Verification:**
  - `dotnet build`
  - `dotnet test`
