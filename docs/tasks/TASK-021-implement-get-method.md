# TASK-021: Implement GET_METHOD

**Status:** Completed
**Completed:** 2026-07-02
**Implemented By:** Codex
**Verification:**
- `./devx build --session d5ff50dc-3e57-47b0-9959-83883ed58637 --format json`
- `./devx test --session d5ff50dc-3e57-47b0-9959-83883ed58637 --format json`
- `./devx method get DevX.Indexer.DotnetBuildTestService.RunBuildAsync --session d5ff50dc-3e57-47b0-9959-83883ed58637 --format json`
**Phase:** v0.2 – Progressive Context Expansion
**Depends On:** TASK-020

## Objective

Implement the `GET_METHOD` protocol operation.

Unlike `GET_METHOD_SUMMARY`, this operation returns the **implementation source for exactly one method**.

The operation must never return an entire file.

---

## Motivation

DevX's context expansion should be:

SEARCH_SYMBOL
→ GET_FILE_SNAPSHOT
→ GET_METHOD_SUMMARY
→ GET_METHOD

Only when an LLM explicitly requests implementation should source code be returned.

---

## Protocol Operation

`GET_METHOD`

---

## CLI

```bash
./devx method get <method> --session <id> --format json
```

Support:

- Method ID
- Qualified method name
- Method name (with ambiguity handling)

---

## Response

Return:

- Method ID
- Qualified name
- File path
- Start line
- End line
- Language
- Implementation source (method only)

Do not return:

- Entire file
- Other methods
- Unrelated code

---

## Session Behaviour

When invoked:

- Record a `method get` operation.
- Add the method to the session working set.
- Mark the method as expanded.

---

## Implementation

1. Add protocol request/response models.
2. Extend storage to retrieve method source.
3. Implement `GET_METHOD` in the ProtocolEngine.
4. Add CLI support.
5. Handle ambiguous matches with structured errors.
6. Update metrics and session state.
7. Add protocol and CLI tests.

---

## Validation

Run:

```bash
./devx index .
SESSION_ID=$(./devx session create --format json | jq -r '.Data.SessionId')

./devx search symbol ProtocolEngine --session "$SESSION_ID" --format json
./devx method summary HandleAsync --session "$SESSION_ID" --format json
./devx method get HandleAsync --session "$SESSION_ID" --format json

./devx build --session "$SESSION_ID" --format json
./devx test --session "$SESSION_ID" --format json
./devx report --session "$SESSION_ID" --format json
```

---

## Acceptance Criteria

- `GET_METHOD` implemented.
- Only the requested method implementation is returned.
- No surrounding methods or full file content is returned.
- Ambiguous requests produce structured errors.
- Session state and metrics updated.
- Build and tests pass.
- `devx report` includes the new operation.

---

## Out of Scope

- AST rewriting
- Multi-method expansion
- Automatic caller/callee expansion

---

## Definition of Done

An AI client can request a single method implementation without receiving the rest of the source file, enabling minimal-context code modification workflows.
