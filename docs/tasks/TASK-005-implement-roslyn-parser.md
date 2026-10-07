# TASK-005: Implement Roslyn Parser

- **Status:** Completed
- **Plan:** PLAN-002 Roslyn Indexer
- **Depends On:** TASK-004

## Objective

Implement the initial C# parser using Roslyn to extract namespaces, types, methods, properties, and fields.

## Prerequisites

- File indexing pipeline works
- `DevX.Indexer` project exists
- `DevX.Storage` can persist indexed metadata

## Inputs

- `docs/plans/PLAN-002-roslyn-indexer.md`
- `docs/specs/SPEC-002-repository-index.md`

## Scope

Extract structural metadata from `.cs` files:

- namespaces
- classes
- interfaces
- structs
- records
- enums
- methods
- constructors
- properties
- fields

## Implementation Steps

1. Add Roslyn package references.
2. Create parser abstraction, such as `ILanguageParser`.
3. Implement `CSharpRoslynParser`.
4. Parse syntax trees for `.cs` files.
5. Extract type declarations.
6. Extract member declarations.
7. Store extracted metadata.
8. Add parser tests with fixture source files.

## Required Metadata

For types:

- name
- namespace
- kind
- visibility
- containing file
- approximate line range

For methods:

- name
- signature
- parameters
- return type
- visibility
- containing type
- approximate line range

## Acceptance Criteria

- Parser extracts C# namespaces.
- Parser extracts C# types.
- Parser extracts methods and constructors.
- Parser extracts properties and fields.
- Parsed metadata is persisted.
- Tests cover common C# declarations.

## Verification Steps

```bash
dotnet test
```

## Out of Scope

- Semantic references
- Call graph generation
- Generic type resolution beyond basic signatures
- Cross-project analysis

## Definition of Done

The indexer can parse C# files and store structural symbol metadata using Roslyn.

## Completion

- **Completed:** 2026-07-02
- **Implemented By:** Codex
- **Verification:**
  - `dotnet build`
  - `dotnet test`
