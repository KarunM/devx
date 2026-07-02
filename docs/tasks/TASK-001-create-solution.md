# TASK-001: Create Solution Structure

- **Status:** Ready
- **Plan:** PLAN-001 Indexing MVP
- **Depends On:** None

## Objective

Create the initial .NET solution structure for the ECP CLI project.

This task establishes the repository skeleton that all later implementation work depends on.

## Prerequisites

- .NET SDK installed
- Repository initialized with Git
- `docs/` directory already contains the ECP vision, ADRs, specs, and plans

## Inputs

- `docs/repository-layout.md`
- `docs/vision.md`
- `docs/specs/SPEC-005-cli-specification.md`

## Files to Create

```text
Ecp.sln
src/
tests/
```

## Implementation Steps

1. Create the root solution file.
2. Create the `src/` directory.
3. Create the `tests/` directory.
4. Ensure the solution name is `Ecp`.
5. Do not create implementation projects in this task unless they are required by tooling.

## Command Example

```bash
dotnet new sln -n Ecp
mkdir -p src
mkdir -p tests
```

## Acceptance Criteria

- `Ecp.sln` exists at the repository root.
- `src/` exists.
- `tests/` exists.
- `dotnet sln list` succeeds.
- No source code has been implemented yet.

## Verification Steps

```bash
dotnet sln list
ls src
ls tests
```

## Out of Scope

- Creating application projects
- Adding NuGet packages
- Implementing CLI behavior
- Creating database schema

## Definition of Done

The repository contains a valid .NET solution with empty `src/` and `tests/` directories ready for project creation.
