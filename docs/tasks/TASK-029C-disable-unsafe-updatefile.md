# TASK-029C: Disable Unsafe UpdateFile for Source Files

**Status:** Ready  
**Priority:** High

## Objective

Prevent destructive whole-file replacement of source files through the ECP patch engine.

Recent attempts showed that `UpdateFile` patches can replace an entire `.cs` file, which is unsafe for normal development and can destroy unrelated code.

---

## Requirements

### Reject whole-file replacement

`PATCH_VALIDATE` and `PATCH_APPLY` must reject any `UpdateFile` operation targeting:

- `*.cs`
- `*.csproj`
- `*.sln`

unless an explicit future override mechanism is introduced.

Return a deterministic validation error such as:

- `UnsafeOperation`
- `Whole-file replacement is not permitted for source files.`

---

### Allowed operations

Continue allowing:

- `CreateFile`
- `DeleteFile` (if already supported)
- non-source asset updates (JSON, Markdown, text, etc.)

Structured operations (existing or future) remain allowed:

- ReplaceMethod
- InsertMethod
- InsertMember
- AddUsing

---

## Tests

Add tests covering:

- UpdateFile on `.cs` fails.
- UpdateFile on `.csproj` fails.
- UpdateFile on `.md` succeeds (if supported).
- ReplaceMethod remains valid.
- CreateFile remains valid.

---

## Validation

```bash
./ecp index .
SESSION_ID=$(./ecp session create --format json | jq -r '.Data.SessionId')

./ecp patch validate tests/patches/updatefile-source.patch.json --session "$SESSION_ID" --format json
./ecp patch validate tests/patches/replace-method.patch.json --session "$SESSION_ID" --format json

./ecp build --session "$SESSION_ID" --format json
./ecp test --session "$SESSION_ID" --format json
./ecp report --session "$SESSION_ID" --format json
```

---

## Acceptance Criteria

- Whole-file replacement of source code is rejected.
- Structured edits continue to work.
- Existing patch functionality is not regressed.
- Tests pass.
