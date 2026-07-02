# TASK-022: Implement GET_TYPE

**Status:** Ready
**Phase:** v0.2 – Progressive Context Expansion
**Depends On:** TASK-021

## Objective

Implement the `GET_TYPE` protocol operation.

Return a metadata-only structural view of a type (class, interface, struct, record, or enum). Do not return implementation source.

## Protocol

`GET_TYPE`

## CLI

```bash
./ecp type get <type> --session <id> --format json
```

Support:
- Type ID
- Qualified type name
- Simple type name (with ambiguity handling)

## Response

Include:

- Type ID
- Name
- Qualified name
- Kind
- Namespace
- Project
- File path
- Base type
- Implemented interfaces
- Generic parameters
- Visibility
- Constructors
- Properties
- Fields
- Methods (metadata only)
- Nested types
- Approximate line count
- Start/end lines

Do not return source code.

## Session Behaviour

- Record `type get`
- Add type to working set
- Mark type as expanded

## Implementation

1. Add request/response models.
2. Extend storage for type lookup.
3. Implement `GET_TYPE` in ProtocolEngine.
4. Add CLI support.
5. Handle ambiguous matches.
6. Update metrics/session.
7. Add tests.

## Validation

```bash
./ecp index .
SESSION_ID=$(./ecp session create --format json | jq -r '.Data.SessionId')

./ecp search symbol ProtocolEngine --session "$SESSION_ID" --format json
./ecp type get ProtocolEngine --session "$SESSION_ID" --format json

./ecp build --session "$SESSION_ID" --format json
./ecp test --session "$SESSION_ID" --format json
./ecp report --session "$SESSION_ID" --format json
```

## Acceptance Criteria

- GET_TYPE implemented.
- CLI supports `./ecp type get`.
- Metadata only.
- No implementation source.
- Session and metrics updated.
- Build/tests pass.
- Report records the operation.

## Definition of Done

An AI can inspect a complete type structure without loading the full source file.
