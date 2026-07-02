# SPEC-006: Storage Model

- Status: Draft
- Version: 1.0

# Purpose

Defines the canonical persistence model for ECP repository state.

The storage model is entity-first. It is not a database schema specification.
SQLite is an implementation detail of one storage backend.

# Goals

- Model repository data as engineering entities rather than tables.
- Preserve stable identifiers across storage implementations.
- Separate repository index data from session state.
- Allow multiple storage backends without changing higher layers.

# Entity Model

## Repository

Top-level engineering container.

Identity:

- `RepositoryId`

Relationships:

- Contains many projects

## Project

Buildable unit within a repository.

Identity:

- `ProjectId`

Relationships:

- Belongs to one repository
- Contains many files

## File

Source or analysis artifact within a project.

Identity:

- `FileId`

Relationships:

- Belongs to one project
- Contains namespaces, symbols, references, diagnostics, and tests

## Namespace

Logical namespace discovered from a file.

Identity:

- `NamespaceId`

Relationships:

- Belongs to one file
- Contains types

## Type

Class, interface, struct, record, or enum.

Identity:

- `TypeId`

Relationships:

- Belongs to one namespace
- Contains methods, properties, and fields

## Method

Executable member declared on a type.

Identity:

- `MethodId`

Relationships:

- Belongs to one type

## Property

Property member declared on a type.

Identity:

- `PropertyId`

Relationships:

- Belongs to one type

## Field

Field member declared on a type.

Identity:

- `FieldId`

Relationships:

- Belongs to one type

## Reference

Usage relationship between engineering symbols.

Identity:

- `ReferenceId`

Relationships:

- Belongs to one file
- Targets a symbol by stable identifier

## Diagnostic

Build, analyzer, or test issue associated with an indexed artifact.

Identity:

- `DiagnosticId`

Relationships:

- Belongs to one repository, project, or file

## Test

Discovered test artifact or test case.

Identity:

- `TestId`

Relationships:

- Belongs to one file or project
- May link to exercised production symbols

# Identity Requirements

- All engineering entities SHALL use stable IDs.
- Storage implementations SHALL NOT expose database row IDs as protocol IDs.
- IDs MAY be GUIDs, ULIDs, or deterministic hashes.
- The chosen format MUST be consistent within a storage implementation.

# Storage Responsibilities

Repository storage is responsible for:

- Persisting repository index entities
- Enforcing referential integrity for index data
- Supporting structural queries
- Managing transactions for indexing operations

Session storage is responsible for:

- Persisting CLI and protocol session state
- Tracking conversational or workflow state
- Storing transient operational metadata

Session storage is not responsible for canonical repository structure.

# Storage Abstractions

The domain layer exposes storage contracts:

- `IRepositoryStore`
- `ISymbolStore`
- `ISessionStore`

Implementations may be provided by:

- SQLite
- Postgres
- In-memory stores
- Remote stores

# Transaction Boundaries

- Repository indexing SHALL be persisted atomically where practical.
- Multi-entity indexing updates SHOULD be committed in a single unit of work.
- Session state updates MAY use separate transactions from repository index updates.

# Index vs Session State

## Index Data

Stored in the canonical repository model:

- Repository metadata
- Projects
- Files
- Namespaces
- Types
- Methods
- Properties
- Fields
- References
- Diagnostics
- Tests

## Session State

Stored separately from the repository index:

- Active repository selection
- Active branch or commit context
- Conversation or orchestration state
- Pending patch or expansion state

# Non-Goals

This specification does not define:

- SQLite DDL
- Query syntax
- Migration strategy
- Parser implementation details

