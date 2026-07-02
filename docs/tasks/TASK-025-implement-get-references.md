# TASK-025: Implement GET_REFERENCES

**Status:** Ready  
**Phase:** v0.2 – Context & Impact Analysis  
**Depends On:** TASK-024

## Objective

Implement the `GET_REFERENCES` protocol operation.

Return metadata describing every indexed reference to a symbol without returning source code.

---

## Motivation

`GET_CALLERS` and `GET_CALLEES` cover methods only.

`GET_REFERENCES` generalizes usage discovery for:

- Types
- Methods
- Properties
- Fields
- Enums

This enables AI agents to answer:

> "Where is this symbol used?"

before making changes.

---

## Protocol

`GET_REFERENCES`

---

## CLI

```bash
./ecp references <symbol> --session <id> --format json
```

Support:

- Symbol ID
- Qualified symbol name
- Simple symbol name (with ambiguity handling)

---

## Response

For each reference include:

- Reference Symbol ID
- Symbol Name
- Qualified Name
- Symbol Kind
- Referencing Method
- Referencing Type
- Namespace
- Project
- File Path
- Line Number (if available)

Do **not** return source code.

---

## Session Behaviour

- Record a `get references` operation.
- Add referenced symbols to the working set.
- Keep the working set unique (no duplicates).
- Update session metrics.

---

## Implementation

1. Extend indexing/storage to persist symbol references.
2. Add protocol request/response models.
3. Implement `GET_REFERENCES` in the ProtocolEngine.
4. Add CLI support.
5. Handle ambiguous symbols.
6. Add protocol and CLI tests.
7. Update report metrics.

---

## Validation

```bash
./ecp index .
SESSION_ID=$(./ecp session create --format json | jq -r '.Data.SessionId')

./ecp search symbol ProtocolEngine --session "$SESSION_ID" --format json
./ecp references ProtocolEngine --session "$SESSION_ID" --format json

./ecp build --session "$SESSION_ID" --format json
./ecp test --session "$SESSION_ID" --format json
./ecp report --session "$SESSION_ID" --format json
```

---

## Acceptance Criteria

- `GET_REFERENCES` implemented.
- CLI supports `./ecp references`.
- Metadata only.
- No implementation source returned.
- Session working set remains unique.
- Session metrics updated.
- Build/tests pass.
- Report records the operation.

---

## Definition of Done

An AI agent can determine every indexed usage of a symbol without opening source files, completing ECP's core repository discovery capabilities.
