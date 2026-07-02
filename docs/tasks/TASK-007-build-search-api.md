# TASK-007: Build Search API

- **Status:** Completed
- **Plan:** PLAN-003 Context Protocol Engine
- **Depends On:** TASK-006

## Objective

Implement repository search operations used by the ECP protocol.

## Prerequisites

- Symbols are indexed
- Files are indexed
- Storage query layer exists

## Inputs

- `docs/specs/SPEC-001-context-protocol.md`
- `docs/specs/SPEC-002-repository-index.md`

## Protocol Operations

```text
SEARCH_SYMBOL
SEARCH_TEXT
```

## Scope

### SEARCH_SYMBOL

Search indexed symbols by name or partial name.

Returns metadata only.

### SEARCH_TEXT

Search file paths and indexed textual metadata.

MVP may avoid full source text indexing unless explicitly approved.

## Implementation Steps

1. Define search request models.
2. Define search response models.
3. Implement symbol search.
4. Implement file/path metadata search.
5. Rank exact matches before partial matches.
6. Add tests for deterministic ordering.

## Response Requirements

Search results must not include implementation source.

Results may include:

- symbol name
- symbol kind
- file path
- containing type
- namespace
- project
- approximate location

## Acceptance Criteria

- `SEARCH_SYMBOL` returns deterministic results.
- `SEARCH_TEXT` returns deterministic metadata results.
- No source code is returned.
- Tests cover exact, partial, and no-result searches.

## Verification Steps

```bash
dotnet test
```

## Out of Scope

- Embeddings
- Full-text source search
- LLM adapter integration
- CLI formatting

## Definition of Done

The storage/query layer can satisfy Level 0 ECP search operations.

## Completion

- **Completed:** 2026-07-02
- **Implemented By:** Codex
- **Verification:**
  - `dotnet build`
  - `dotnet test`
