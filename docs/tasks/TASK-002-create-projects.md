# TASK-002: Create Projects

- **Status:** Ready
- **Plan:** PLAN-001 Indexing MVP
- **Depends On:** TASK-001

## Objective

Create the initial project structure for the ECP system.

Each project should have a clear responsibility and avoid circular dependencies.

## Prerequisites

- `Ecp.sln` exists
- `src/` and `tests/` directories exist

## Inputs

- `docs/repository-layout.md`
- `docs/specs/SPEC-005-cli-specification.md`
- `docs/glossary.md`

## Projects to Create

```text
src/Ecp.Cli/
src/Ecp.Core/
src/Ecp.Protocol/
src/Ecp.Indexer/
src/Ecp.Storage/
src/Ecp.Patching/
src/Ecp.Build/

tests/Ecp.Tests/
```

## Responsibilities

### Ecp.Cli

Command-line entry point.

### Ecp.Core

Shared domain abstractions and core models.

### Ecp.Protocol

Protocol request and response models.

### Ecp.Indexer

Repository indexing pipeline and parser abstractions.

### Ecp.Storage

SQLite persistence and query layer.

### Ecp.Patching

Structured patch validation and application.

### Ecp.Build

Build, test, and diagnostics orchestration.

### Ecp.Tests

Unit and integration tests.

## Implementation Steps

1. Create class library projects for core libraries.
2. Create console project for `Ecp.Cli`.
3. Create test project for `Ecp.Tests`.
4. Add all projects to the solution.
5. Configure references.

## Suggested References

```text
Ecp.Cli -> Ecp.Core, Ecp.Protocol, Ecp.Indexer, Ecp.Storage, Ecp.Patching, Ecp.Build

Ecp.Protocol -> Ecp.Core

Ecp.Indexer -> Ecp.Core, Ecp.Storage

Ecp.Storage -> Ecp.Core

Ecp.Patching -> Ecp.Core

Ecp.Build -> Ecp.Core

Ecp.Tests -> all production projects as needed
```

## Acceptance Criteria

- All projects exist.
- All projects are included in `Ecp.sln`.
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

The solution contains all initial ECP projects with clean project references and a successful build.
