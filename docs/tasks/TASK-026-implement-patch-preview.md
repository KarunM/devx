# TASK-026: Implement PATCH_PREVIEW

**Status:** Ready  
**Phase:** v0.2 – Safe Editing  
**Depends On:** TASK-025

## Objective

Implement the `PATCH_PREVIEW` protocol operation.

Generate a preview of a patch without modifying the repository.

---

## Motivation

Before applying changes, an AI (or developer) should be able to inspect the proposed edits.

Workflow:

```
SEARCH
→ SNAPSHOT
→ TYPE
→ METHOD SUMMARY
→ METHOD
→ PATCH_PREVIEW
→ PATCH_APPLY
```

---

## Protocol

`PATCH_PREVIEW`

---

## CLI

```bash
./ecp patch preview <patch-file> --session <id> --format json
```

---

## Response

Return metadata only:

- Patch valid (true/false)
- Files affected
- Operations detected
- Unified diff preview
- Validation errors (if any)

Do **not** modify repository files.

---

## Session Behaviour

- Record a `patch preview` operation.
- Do not update repository contents.
- Update session metrics.

---

## Implementation

1. Add protocol request/response models.
2. Reuse existing patch parser.
3. Generate unified diff previews.
4. Validate paths and targets.
5. Wire ProtocolEngine.
6. Add CLI command.
7. Add tests.
8. Update report metrics.

---

## Validation

```bash
./ecp index .
SESSION_ID=$(./ecp session create --format json | jq -r '.Data.SessionId')

./ecp patch preview sample.patch.json --session "$SESSION_ID" --format json

./ecp build --session "$SESSION_ID" --format json
./ecp test --session "$SESSION_ID" --format json
./ecp report --session "$SESSION_ID" --format json
```

---

## Acceptance Criteria

- `PATCH_PREVIEW` implemented.
- Repository remains unchanged.
- Preview includes unified diff.
- Validation errors are reported.
- CLI supports `./ecp patch preview`.
- Tests pass.
- Report records the operation.

---

## Definition of Done

An AI can preview and validate a patch before applying it, enabling safe review and reducing failed edit attempts.
