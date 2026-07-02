# TASK-006: Index Symbols

- **Status:** Ready
- **Plan:** PLAN-002 Roslyn Indexer
- **Depends On:** TASK-005

## Objective

Persist indexed symbols in a queryable form and establish stable symbol identity.

## Prerequisites

- Roslyn parser extracts symbols
- SQLite schema supports symbols
- Basic indexing pipeline works

## Inputs

- `docs/specs/SPEC-002-repository-index.md`
- `docs/glossary.md`

## Scope

- Define symbol identity model
- Persist symbols consistently
- Support symbol lookup by name
- Support symbol lookup by qualified name
- Link symbols to files and containing symbols

## Symbol Identity

Symbol identity should be stable enough for protocol operations.

Suggested identity inputs:

```text
repository
project
file path
namespace
containing type
symbol kind
symbol name
signature
```

## Implementation Steps

1. Create core symbol model.
2. Normalize symbol names.
3. Compute stable symbol identifiers.
4. Persist symbols during indexing.
5. Add query methods for symbol lookup.
6. Add tests for duplicate names in different namespaces/types.

## Acceptance Criteria

- Symbols are uniquely identifiable.
- Duplicate short names are supported.
- Qualified lookup works.
- Symbol-to-file relationships are stored.
- Symbol-to-containing-type relationships are stored.

## Verification Steps

```bash
dotnet test
```

## Out of Scope

- Fuzzy search
- Semantic embeddings
- Call graph
- References

## Definition of Done

Indexed symbols can be reliably stored, identified, and retrieved by name or qualified name.
