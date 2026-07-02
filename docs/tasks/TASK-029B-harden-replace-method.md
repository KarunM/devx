# TASK-029B: Harden ReplaceMethod Patch Operation

**Status:** Ready  
**Phase:** Patch Engine Reliability

## Objective

Strengthen the existing `ReplaceMethod` structured patch operation so it is reliable enough for day-to-day feature development without replacing entire source files.

This task intentionally focuses on a single operation rather than redesigning the entire patch engine.

---

## Scope

Improve only the existing `ReplaceMethod` operation.

Do **not** introduce:

- InsertMethod
- InsertMember
- ReplaceBlock
- AddUsing
- Cross-file semantic editing

Those will be handled by later tasks.

---

## Requirements

### Preview

`PATCH_PREVIEW` must:

- recognize `ReplaceMethod`
- identify affected files
- show a unified diff
- report validation errors without applying changes

---

### Validate

`PATCH_VALIDATE` must verify:

- repository-root containment
- target file exists
- target type exists
- target method exists
- method uniquely resolves
- replacement source is non-empty

Reject the patch if any validation fails.

---

### Apply

`PATCH_APPLY` must:

- replace only the selected method
- preserve all surrounding members
- preserve unrelated formatting where practical
- fail atomically if replacement cannot be completed

Whole-file replacement is not allowed.

---

### Rollback

Rollback must either:

- restore the previous method body, or
- clearly report that rollback is unsupported.

Never report success unless the rollback actually completed.

---

## Test Fixtures

Create:

```
tests/patches/replace-method.patch.json
tests/patches/replace-method-invalid.patch.json
```

---

## Tests

Add automated tests covering:

- valid ReplaceMethod
- missing target method
- ambiguous target
- outside repository path
- preview succeeds
- validate succeeds
- apply succeeds
- neighboring methods remain unchanged

---

## Validation

```bash
./ecp index .
SESSION=$(./ecp session create --format json)

./ecp patch preview tests/patches/replace-method.patch.json --session <id> --format json
./ecp patch validate tests/patches/replace-method.patch.json --session <id> --format json
./ecp patch apply tests/patches/replace-method.patch.json --session <id> --format json

./ecp build --session <id> --format json
./ecp test --session <id> --format json
./ecp report --session <id> --format json
```

---

## Acceptance Criteria

- ReplaceMethod is reliable.
- No whole-file replacement occurs.
- Tests pass.
- Preview, validate, and apply all succeed for ReplaceMethod.
- Failure cases produce deterministic errors.

---

## Follow-up Tasks

- TASK-029C — InsertMethod
- TASK-029D — AddUsing
- TASK-029E — InsertMember
