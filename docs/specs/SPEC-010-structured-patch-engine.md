# SPEC-010: Structured Patch Engine

**Status:** Draft  
**Phase:** DevX Editing Layer  
**Depends On:** SPEC-009

## Objective

Make DevX the primary editing surface for agents by supporting safe, structured source edits through patch preview, validation, apply, and rollback.

Agents should avoid IDE Apply Patch for repository source when the required edit can be represented as an DevX patch.

## Motivation

DevX now supports deterministic source reads:

```text
search symbol → read --id
```

The next gap is editing.

Agents still fall back to direct IDE patching because the patch engine cannot yet express common implementation edits safely.

This spec defines the structured patch operations needed for normal feature work.

## Principles

- Source edits should be structured, not whole-file replacement.
- Whole-file `UpdateFile` for source files remains unsafe and should stay blocked.
- Patches must be previewable.
- Patches must be validatable.
- Patches must apply only the intended code region.
- Failed patches must not leave partial edits.
- Rollback must either work or clearly report unsupported.

## Supported Operations

### CreateFile

Create a new file.

Required fields:

- operation: `CreateFile`
- path
- content

Allowed for source and non-source files.

### DeleteFile

Delete an existing file.

Required fields:

- operation: `DeleteFile`
- path

Must validate that the path is inside the repository root.

### ReplaceMethod

Replace exactly one existing method.

Required fields:

- operation: `ReplaceMethod`
- path
- methodName or methodId
- replacementText

Validation:

- target file exists
- target method exists
- target method resolves uniquely
- replacement text is non-empty
- only the selected method changes
- neighboring methods remain unchanged

### Text replace

Replace an exact text fragment in a file. The canonical operation is `ReplaceText`; `TextReplace` is accepted as an alias. Supply `oldText` and `replacementText`. By default the old text must match exactly once. Use `occurrence` (one-based) to select one match or `replaceAll: true` to replace every match. Validation and apply report the number of matches when the text is absent or ambiguous. Rollback restores the captured before-state.

### InsertMethod

Insert a new method into an existing type.

Required fields:

- operation: `InsertMethod`
- path
- containingType or typeId
- methodText
- position:
  - `EndOfType`
  - `Before:<methodName>`
  - `After:<methodName>`

Validation:

- target file exists
- target type exists
- insertion point exists
- method text is non-empty
- method does not already exist unless explicitly allowed

### AddUsing

Add a using/import directive if missing.

Required fields:

- operation: `AddUsing`
- path
- namespace

Validation:

- target file exists
- namespace is non-empty
- duplicate using is not added
- using is inserted in the correct location

## Explicitly Unsafe

The patch engine must continue rejecting:

```json
{
  "operation": "UpdateFile",
  "path": "src/Example.cs"
}
```

for:

- `.cs`
- `.csproj`
- `.sln`

unless a future explicit override is introduced.

## CLI

Existing commands remain:

```bash
./devx patch preview <patch-file> --session <id> --format json
./devx patch validate <patch-file> --session <id> --format json
./devx patch apply <patch-file> --session <id> --format json
./devx patch rollback <patch-file> --session <id> --format json
```

## Patch File Examples

### ReplaceMethod

```json
{
  "version": "1.0",
  "operations": [
    {
      "operation": "ReplaceMethod",
      "path": "src/DevX.Protocol/ProtocolEngine.cs",
      "methodName": "HandlePatchAsync",
      "replacementText": "private async Task<ProtocolResponse> HandlePatchAsync(...)\n{\n    ...\n}"
    }
  ]
}
```

### Text replace

Replace an exact text fragment in a file. The canonical operation is `ReplaceText`; `TextReplace` is accepted as an alias. Supply `oldText` and `replacementText`. By default the old text must match exactly once. Use `occurrence` (one-based) to select one match or `replaceAll: true` to replace every match. Validation and apply report the number of matches when the text is absent or ambiguous. Rollback restores the captured before-state.

### InsertMethod

```json
{
  "version": "1.0",
  "operations": [
    {
      "operation": "InsertMethod",
      "path": "src/DevX.Cli/Program.cs",
      "containingType": "Program",
      "position": "EndOfType",
      "methodText": "static int NewCommand()\n{\n    return 0;\n}"
    }
  ]
}
```

### AddUsing

```json
{
  "version": "1.0",
  "operations": [
    {
      "operation": "AddUsing",
      "path": "src/DevX.Protocol/ProtocolEngine.cs",
      "namespace": "System.Text.RegularExpressions"
    }
  ]
}
```

## Preview Requirements

`patch preview` must return:

- valid/invalid
- files affected
- operations detected
- unified diff preview
- validation errors
- warnings

Preview must not mutate files.

## Validate Requirements

`patch validate` must return:

- valid
- warnings
- errors
- files count
- operations count

Validation must catch:

- path escape
- missing file
- missing method/type
- ambiguous target
- unsupported operation
- unsafe source-file whole replacement
- empty replacement text
- duplicate using

## Apply Requirements

`patch apply` must:

- validate before applying
- apply operations in order
- prevent path escapes
- avoid partial application where possible
- report structured errors
- record metrics

## Rollback Requirements

Rollback should support at least:

- CreateFile
- DeleteFile where backup exists
- ReplaceMethod where before-state is available
- InsertMethod where inserted method can be uniquely removed
- AddUsing where inserted using can be uniquely removed

If rollback is unsupported for an operation, it must return a deterministic error.

## Fixtures

Add fixtures under:

```text
tests/patches/
```

Required fixtures:

- `create-file.patch.json`
- `delete-file.patch.json`
- `replace-method.patch.json`
- `replace-method-invalid.patch.json`
- `insert-method.patch.json`
- `insert-method-invalid.patch.json`
- `add-using.patch.json`
- `add-using-duplicate.patch.json`
- `updatefile-source.patch.json`
- `invalid-path.patch.json`

## Tests

Add tests for:

- preview valid ReplaceMethod
- validate valid ReplaceMethod
- apply valid ReplaceMethod
- ReplaceMethod preserves neighboring methods
- ReplaceMethod rejects ambiguous target
- InsertMethod works
- InsertMethod rejects missing type
- AddUsing works
- AddUsing avoids duplicate
- UpdateFile source rejection still works
- invalid path rejection still works
- rollback behavior is explicit

## Metrics

`devx report` should include patch operation counts:

- patch preview count
- patch validate count
- patch apply count
- patch rollback count
- structured operations applied
- failed patch operations

## Acceptance Criteria

- Agents can use DevX patches for common source edits.
- Whole-source-file replacement remains blocked.
- Preview, validate, apply, and rollback work for supported operations.
- Tests cover success and failure paths.
- Build succeeds.
- Tests succeed.
- New patch commands are executed successfully at least once.

## Definition of Done

An agent can read code through DevX, produce a structured DevX patch, preview it, validate it, apply it, and verify the result without using IDE Apply Patch for normal source edits.
