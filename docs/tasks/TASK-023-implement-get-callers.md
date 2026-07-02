# TASK-023: Implement GET_CALLERS

**Status:** Ready  
**Phase:** v0.2 – Context & Impact Analysis  
**Depends On:** TASK-022

## Objective

Implement the `GET_CALLERS` protocol operation.

`GET_CALLERS` returns metadata describing every indexed method that calls a target method.

Do **not** return source code.

---

## Motivation

Before modifying a method, an AI should understand its impact.

Typical workflow:

```
SEARCH_SYMBOL
    ↓
GET_TYPE
    ↓
GET_METHOD_SUMMARY
    ↓
GET_CALLERS
    ↓
GET_METHOD
```

---

## Protocol

`GET_CALLERS`

---

## CLI

```bash
./ecp callers <method> --session <id> --format json
```

Support:

- Method ID
- Qualified method name
- Method name (with ambiguity handling)

---

## Response

For each caller include:

- Caller Method ID
- Caller Method Name
- Qualified Name
- Namespace
- Project
- File Path
- Containing Type
- Call Site Line (if available)

Do **not** return method implementation.

---

## Session Behaviour

- Record a `get callers` operation.
- Add discovered callers to the working set.
- Update session metrics.

---

## Implementation

1. Extend indexing/storage to record caller relationships.
2. Add request/response models.
3. Implement `GET_CALLERS` in ProtocolEngine.
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
./ecp callers HandleAsync --session "$SESSION_ID" --format json

./ecp build --session "$SESSION_ID" --format json
./ecp test --session "$SESSION_ID" --format json
./ecp report --session "$SESSION_ID" --format json
```

---

## Acceptance Criteria

- `GET_CALLERS` implemented.
- CLI supports `./ecp callers`.
- Metadata only.
- No source code returned.
- Session and metrics updated.
- Build/tests pass.
- Report records the operation.

---

## Definition of Done

An AI can determine every indexed caller of a method without opening source files.
