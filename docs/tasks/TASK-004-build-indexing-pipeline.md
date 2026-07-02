# TASK-004: Build Indexing Pipeline

- **Status:** Completed
- **Plan:** PLAN-001 Indexing MVP
- **Depends On:** TASK-003

## Objective

Implement the repository indexing pipeline that walks a repository, discovers projects and source files, and stores file-level metadata.

This task creates the indexing flow before detailed C# symbol parsing is added.

## Prerequisites

- SQLite schema exists
- Storage initializer works
- `Ecp.Indexer` project exists
- `Ecp.Indexer` project exists

## Inputs

- `docs/specs/SPEC-002-repository-index.md`
- `docs/plans/PLAN-001-indexing-mvp.md`

## Scope

- Discover repository root
- Discover project files
- Discover source files
- Store repository metadata
- Store project metadata
- Store file metadata
- Exclude common generated/build directories

## Directories to Ignore

```text
.git
bin
obj
.vs
.idea
node_modules
dist
out
coverage
```

## Implementation Steps

1. Create an indexing service abstraction.
2. Implement repository traversal.
3. Detect `.csproj` files.
4. Detect `.cs` files.
5. Store discovered entities in SQLite.
6. Add CLI-accessible service method, but do not implement CLI command yet.
7. Add tests using a small fixture repository.

## Acceptance Criteria

- Indexer can scan a repository path.
- Repository row is created.
- Project rows are created.
- File rows are created.
- Ignored directories are skipped.
- Indexing the same repository twice does not create duplicate rows.

## Verification Steps

```bash
dotnet test
```

## Out of Scope

- Roslyn symbol extraction
- Call graph generation
- Text search
- Protocol handlers
- CLI command implementation

## Definition of Done

The indexing pipeline can populate repository, project, and file metadata into SQLite from a local repository path.

## Completion

- **Completed:** 2026-07-02
- **Implemented By:** Codex
- **Verification:**
  - `dotnet build`
  - `dotnet test`
