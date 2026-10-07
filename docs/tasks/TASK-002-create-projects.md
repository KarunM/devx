# TASK-002: Create Projects

- **Status:** Completed
- **Plan:** PLAN-001 Indexing MVP
- **Depends On:** TASK-001

## Objective

Create the initial project structure for the DevX system.

Each project should have a clear responsibility and avoid circular dependencies.

## Prerequisites

- `DevX.sln` exists
- `src/` and `tests/` directories exist

## Inputs

- `docs/repository-layout.md`
- `docs/specs/SPEC-005-cli-specification.md`
- `docs/glossary.md`

## Projects to Create

```text
src/DevX.Cli/
src/DevX.Core/
src/DevX.Protocol/
src/DevX.Indexer/
src/DevX.Storage/
src/DevX.Patching/
src/DevX.Build/

tests/DevX.Tests/
```

## Responsibilities

### DevX.Cli

Command-line entry point.

### DevX.Core

Shared domain abstractions and core models.

### DevX.Protocol

Protocol request and response models.

### DevX.Indexer

Repository indexing pipeline and parser abstractions.

### DevX.Storage

SQLite persistence and query layer.

### DevX.Patching

Structured patch validation and application.

### DevX.Build

Build, test, and diagnostics orchestration.

### DevX.Tests

Unit and integration tests.

## Implementation Steps

1. Create class library projects for core libraries.
2. Create console project for `DevX.Cli`.
3. Create test project for `DevX.Tests`.
4. Add all projects to the solution.
5. Configure references.

## Suggested References

```text
DevX.Cli -> DevX.Core, DevX.Protocol, DevX.Indexer, DevX.Storage, DevX.Patching, DevX.Build

DevX.Protocol -> DevX.Core

DevX.Indexer -> DevX.Core, DevX.Storage

DevX.Storage -> DevX.Core

DevX.Patching -> DevX.Core

DevX.Build -> DevX.Core

DevX.Tests -> all production projects as needed
```

## Acceptance Criteria

- All projects exist.
- All projects are included in `DevX.sln`.
- Project references compile.
- `dotnet build` succeeds.

## Verification Steps

```bash
dotnet sln list
dotnet build
```

## Out of Scope

- Implementing business logic
- Creating SQLite tables
- Implementing protocol handlers
- Implementing CLI commands

## Definition of Done

The solution contains all initial DevX projects with clean project references and a successful build.

## Completion

- **Completed:** 2026-07-02
- **Implemented By:** Codex
- **Verification:**
  - `dotnet sln DevX.sln list`
  - `dotnet build`
