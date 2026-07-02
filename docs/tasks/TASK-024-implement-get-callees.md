# TASK-024: Implement GET_CALLEES

**Status:** Completed
**Completed:** 2026-07-02
**Implemented By:** Codex
**Verification:**
- `./ecp build --session 8b5b0b3c-efe2-4e84-b98c-0a41b1c3c0a4 --format json`
- `./ecp test --session 8b5b0b3c-efe2-4e84-b98c-0a41b1c3c0a4 --format json`
- `./ecp callees Ecp.Indexer.DotnetBuildTestService.RunAsync --session 8b5b0b3c-efe2-4e84-b98c-0a41b1c3c0a4 --format json`
**Phase:** v0.2 – Context & Impact Analysis
**Depends On:** TASK-023

## Objective

Implement the `GET_CALLEES` protocol operation.

Return metadata describing every indexed method directly called by a target method.

Do **not** return source code.

---

## Motivation

After `GET_CALLERS`, ECP also needs to answer:

> What does this method depend on?

This allows an AI agent to understand downstream dependencies before making changes.

Progressive workflow:

SEARCH_SYMBOL
→ GET_FILE_SNAPSHOT
→ GET_TYPE
→ GET_METHOD_SUMMARY
→ GET_METHOD
→ GET_CALLERS
→ GET_CALLEES

---

## Protocol

`GET_CALLEES`

---

## CLI

```bash
./ecp callees <method> --session <id> --format json
```

Support:

- Method ID
- Qualified method name
- Method name (with ambiguity handling)

---

## Response

For each callee return:

- Method ID
- Method name
- Qualified name
- Namespace
- Project
- File path
- Containing type
- Call site line (if available)

Do **not** return implementation source.

---

## Session Behaviour

- Record a `get callees` operation.
- Add discovered methods to the session working set.
- Update session metrics.

---

## Implementation

1. Extend indexing/storage to persist callee relationships.
2. Add protocol request/response models.
3. Implement `GET_CALLEES` in the ProtocolEngine.
4. Add CLI command.
5. Handle ambiguous methods.
6. Add tests.
7. Update report metrics.

---

## Validation

```bash
./ecp index .
SESSION_ID=$(./ecp session create --format json | jq -r '.Data.SessionId')

./ecp search symbol HandleAsync --session "$SESSION_ID" --format json
./ecp callees HandleAsync --session "$SESSION_ID" --format json

./ecp build --session "$SESSION_ID" --format json
./ecp test --session "$SESSION_ID" --format json
./ecp report --session "$SESSION_ID" --format json
```

---

## Acceptance Criteria

- `GET_CALLEES` implemented.
- CLI supports `./ecp callees`.
- Metadata only.
- No source code returned.
- Session and metrics updated.
- Build/tests pass.
- Report records the operation.

---

## Definition of Done

An AI can determine every indexed dependency directly called by a method without opening additional source files.
