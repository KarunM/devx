# TASK-003: Create SQLite Schema

- **Status:** Completed
- **Plan:** PLAN-001 Indexing MVP
- **Depends On:** TASK-002

## Objective

Create the initial repository storage foundation for the index.

The schema should persist the canonical engineering model without relying on embeddings or vector storage.

## Prerequisites

- `Ecp.Infrastructure` project exists
- `Ecp.Domain` project exists
- Solution builds successfully

## Inputs

- `docs/specs/SPEC-006-storage-model.md`
- `docs/specs/SPEC-002-repository-index.md`
- `docs/adr/ADR-002-local-repository-discovery.md`
- `docs/adr/ADR-003-progressive-context.md`

## Tables to Create

```text
Repository
Project
File
Namespace
Type
Method
Property
Field
Reference
Diagnostic
Test
Session
```

## Required Design Properties

- Stable entity identifiers
- Foreign keys between related entities
- File paths stored relative to repository root
- Symbol identity preserved across queries
- Full source code should not be stored in the MVP schema
- Session state separated from repository index data

## Implementation Steps

1. Define storage contracts in `Ecp.Domain`.
2. Add SQLite package dependency to `Ecp.Infrastructure`.
3. Create a database initializer and storage bootstrapper.
4. Create schema migration or bootstrap SQL.
5. Create indexes for common lookups:
   - file path
   - symbol name
   - type name
   - method name
   - reference target
6. Add a basic integration test that initializes a new database.

## Acceptance Criteria

- A new SQLite database can be created.
- All MVP entities can be persisted.
- Foreign keys are enabled.
- Re-running initialization is safe.
- Tests verify schema creation.

## Verification Steps

```bash
dotnet test
```

Optionally inspect the database:

```bash
sqlite3 .ecp/index.db ".tables"
```

## Out of Scope

- Data insertion pipeline
- Roslyn parsing
- Search API
- Embeddings
- Git history

## Definition of Done

`Ecp.Infrastructure.Storage.SQLite` can initialize an empty ECP repository database with all required MVP entities.

## Completion

- **Completed:** 2026-07-02
- **Implemented By:** Codex
- **Verification:**
  - `dotnet build`
  - `dotnet test`
