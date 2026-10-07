# TASK-029A: Improve Patch Engine for Structured Multi-File Code Changes

**Status:** Ready  
**Phase:** v0.3 – Patch Engine Reliability  
**Blocks:** TASK-029  
**Reason:** Current patch format cannot safely express the required incremental-indexing edits without replacing entire files.

---

## Objective

Improve the DevX patch engine so agents can make precise, structured multi-file code changes without using unsafe whole-file replacement.

This task exists because TASK-029 exposed a limitation: the current patch model can express simple file creation and basic replacement, but it is not expressive enough for safe feature implementation across multiple C# files.

---

## Problem

During TASK-029, the agent attempted to encode implementation changes using an `UpdateFile`-style patch that would replace an entire source file.

That is unsafe because it can:

- delete unrelated declarations
- remove existing APIs
- break formatting
- overwrite concurrent changes
- bypass semantic validation
- make large diffs hard to review

DevX needs more precise structured edit operations.

---

## Required Patch Operations

Add or strengthen support for:

### ReplaceMethod

Replace the body or full declaration of an existing method.

Required fields:

- file path
- containing type
- method name or method id
- replacement source

### InsertMethod

Insert a new method into an existing type.

Required fields:

- file path
- containing type
- insertion position:
  - before method
  - after method
  - end of type
- method source

### InsertMember

Insert a property, field, constructor, nested type, or other member into a type.

Required fields:

- file path
- containing type
- member kind
- insertion position
- member source

### ReplaceBlock

Replace a named or line-bounded block only when no better semantic operation exists.

Required fields:

- file path
- start line
- end line
- replacement source

This should be treated as lower confidence than method/member operations.

### AddUsing

Add a `using` directive if missing.

Required fields:

- file path
- namespace

Must avoid duplicates.

---

## Validation Requirements

Every structured patch operation must validate:

- repository-root containment
- target file exists
- target type exists
- target method/member exists when required
- insertion point exists when specified
- operation does not affect unrelated code
- operation does not produce invalid syntax where syntax validation is available

---

## Preview Requirements

`PATCH_PREVIEW` must support the new operations.

Preview should show:

- files affected
- operations detected
- validation status
- unified diff preview
- warnings/errors

---

## Validate Requirements

`PATCH_VALIDATE` must support the new operations.

Validation should fail before apply when:

- target symbol is missing
- target is ambiguous
- file path escapes repository root
- operation is unsupported
- operation would replace more than intended

---

## Apply Requirements

`PATCH_APPLY` must support the new operations.

Apply must:

- modify only the intended region
- preserve unrelated source
- preserve repository-root safety
- fail cleanly on ambiguity
- avoid partial application where possible

---

## Rollback Requirements

Where possible, rollback must support the new operations.

At minimum:

- store enough before-state to revert structured edits
- report unsupported rollback clearly
- avoid pretending rollback succeeded if not supported

---

## CLI

No new top-level CLI command is required.

Existing commands must support the new patch operations:

```bash
./devx patch preview <patch-file> --session <id> --format json
./devx patch validate <patch-file> --session <id> --format json
./devx patch apply <patch-file> --session <id> --format json
./devx patch rollback <patch-file> --session <id> --format json
```

---

## Test Fixtures

Add reusable patch fixtures under:

```text
tests/patches/
```

Suggested fixtures:

- `replace-method.patch.json`
- `insert-method.patch.json`
- `insert-member.patch.json`
- `add-using.patch.json`
- `invalid-target-method.patch.json`
- `invalid-path.patch.json`

---

## Acceptance Criteria

- DevX can preview structured multi-file patches.
- DevX can validate structured multi-file patches.
- DevX can apply structured edits without replacing whole files.
- Existing patch operations still work.
- Unsafe whole-file replacement is not required for normal feature work.
- Tests cover success and failure cases.
- `dotnet build` succeeds.
- `dotnet test` succeeds.
- `devx report` records preview/validate/apply operations.

---

## Validation Commands

```bash
./devx index .
SESSION_ID=$(./devx session create --format json | jq -r '.Data.SessionId')

./devx patch preview tests/patches/insert-method.patch.json --session "$SESSION_ID" --format json
./devx patch validate tests/patches/insert-method.patch.json --session "$SESSION_ID" --format json
./devx patch apply tests/patches/insert-method.patch.json --session "$SESSION_ID" --format json

./devx build --session "$SESSION_ID" --format json
./devx test --session "$SESSION_ID" --format json
./devx report --session "$SESSION_ID" --format json
```

If `jq` is unavailable, copy the session ID manually.

---

## Out of Scope

- Full semantic refactoring
- Rename symbol
- Cross-language structured patching
- Formatting engine
- LSP integration

---

## Definition of Done

Agents can express normal implementation changes as structured DevX patches instead of direct IDE edits or unsafe whole-file replacement.

Once this task is complete, resume TASK-029: Incremental Indexing.
