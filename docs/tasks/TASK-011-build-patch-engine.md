# TASK-011: Build Patch Engine

- **Status:** Ready
- **Plan:** PLAN-005 Patch Engine
- **Depends On:** TASK-010

## Objective

Implement structured patch operations that can be validated and applied safely.

## Prerequisites

- Protocol dispatcher exists
- CLI exists
- Symbol index exists

## Inputs

- `docs/specs/SPEC-004-patch-model.md`
- `docs/adr/ADR-005-structured-patch-model.md`
- `docs/plans/PLAN-005-patch-engine.md`

## Patch Operations

MVP operations:

```text
CreateFile
DeleteFile
ReplaceMethod
InsertMethod
DeleteMethod
```

Future operations:

```text
ReplaceType
RenameSymbol
MoveType
UpdateProjectFile
```

## Implementation Steps

1. Define patch operation models.
2. Define patch validation result.
3. Implement patch validator.
4. Implement patch applier.
5. Require explicit target file/symbol.
6. Preserve original file formatting where possible.
7. Add rollback or backup strategy.
8. Add tests using fixture files.

## Validation Rules

A patch must fail if:

- target file does not exist
- target symbol is ambiguous
- operation type is unsupported
- replacement would create invalid file state
- patch attempts to modify files outside repository root

## Acceptance Criteria

- Patch model is machine-readable.
- Invalid patches are rejected.
- Valid patches apply deterministically.
- File boundaries are enforced.
- Tests cover success and failure cases.

## Verification Steps

```bash
dotnet test
```

## Out of Scope

- Free-form diff application
- IDE integration
- Automatic code generation
- Semantic refactoring

## Definition of Done

The patch engine can validate and apply MVP structured patch operations safely.
