# TASK-016: Implement GET_FILE_SNAPSHOT

**Status:** Ready  
**Phase:** v0.2 – Context Expansion

## Objective

Implement the `GET_FILE_SNAPSHOT` protocol operation that returns a **metadata-only** view of a source file.

This is the first context expansion operation in ECP.

## Why

Current flow:

SEARCH_SYMBOL → PATCH

Target flow:

SEARCH_SYMBOL
    ↓
GET_FILE_SNAPSHOT
    ↓
GET_TYPE
    ↓
GET_METHOD_SUMMARY
    ↓
GET_METHOD
    ↓
PATCH

## Inputs

- Session ID
- Repository-relative file path or FileId

## Output

Return only:

- File path
- Project
- Language
- Approximate LOC
- Namespaces
- Types
- Methods
- Properties
- Fields
- Dependencies

**Do not return source code.**

## CLI

```bash
ecp snapshot <file> --session <id> --format json
```

## Protocol Operation

```
GET_FILE_SNAPSHOT
```

## Implementation

1. Add GET_FILE_SNAPSHOT request/response models.
2. Extend ProtocolEngine.
3. Read metadata from the repository index.
4. Update session working set.
5. Add CLI command.
6. Add unit and integration tests.

## Acceptance Criteria

- Metadata only.
- No implementation returned.
- Session updated.
- `dotnet build` passes.
- `dotnet test` passes.

## Definition of Done

The CLI and ProtocolEngine support progressive file discovery without exposing implementation code.
