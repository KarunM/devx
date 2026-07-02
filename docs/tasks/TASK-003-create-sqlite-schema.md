# TASK-003: Create SQLite Schema

- **Status:** Ready
- **Plan:** PLAN-001 Indexing MVP
- **Depends On:** TASK-002

## Objective

Create the initial SQLite schema for the repository index.

The schema should support structural indexing for the MVP without relying on embeddings or vector storage.

## Prerequisites

- `Ecp.Storage` project exists
- `Ecp.Core` project exists
- Solution builds successfully

## Inputs

- `docs/specs/SPEC-002-repository-index.md`
- `docs/adr/ADR-002-local-repository-discovery.md`
- `docs/adr/ADR-003-progressive-context.md`

## Tables to Create

```text
Repositories
Projects
Files
Namespaces
Types
Methods
Properties
Fields
References
CallGraph
Tests
Diagnostics
```

## Required Design Properties

- Stable primary keys
- Foreign keys between related entities
- File paths stored relative to repository root
- Symbol identity preserved across queries
- Full source code should not be stored in the MVP schema

## Implementation Steps

1. Add SQLite package dependency to `Ecp.Storage`.
2. Create a database initializer.
3. Create schema migration or bootstrap SQL.
4. Create indexes for common lookups:
   - file path
   - symbol name
   - type name
   - method name
   - reference target
5. Add a basic integration test that initializes a new database.

## Acceptance Criteria

- A new SQLite database can be created.
- All MVP tables are created.
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

`Ecp.Storage` can initialize an empty ECP repository index database with all required MVP tables.
