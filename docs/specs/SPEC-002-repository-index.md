# SPEC-002: Repository Index

- **Status:** Draft
- **Version:** 1.0

# Purpose

The Repository Index defines the canonical structural representation of a source repository exposed by the Engineering Context Protocol (DevX).

The index provides deterministic repository discovery without exposing source code unless explicitly requested.

# Goals

- Represent repository structure independently of any LLM.
- Support fast structural queries.
- Enable progressive context retrieval.
- Remain language-agnostic through pluggable parsers.

# Core Entities

## Repository

Represents a source repository.

Attributes:

- Name
- Root Path
- Branch (optional)
- Commit (optional)

## Project

Represents a buildable project.

Attributes:

- Name
- Path
- Language
- Framework
- Dependencies

## Folder

Represents a directory hierarchy.

## File

Represents a source file.

Attributes:

- Path
- Language
- Approximate LOC
- Namespace(s)
- Contained Types

Whole-repository and file updates remove indexed paths missing on disk; doctor lists every such path and recommends `devx index .` to remove them.

## Namespace

Logical grouping of types.

## Type

Represents:

- Class
- Interface
- Struct
- Record
- Enum

Attributes:

- Name
- Kind
- Namespace
- Base Type
- Implemented Interfaces
- Visibility

## Method

Represents executable behavior.

Attributes:

- Name
- Signature
- Parameters
- Return Type
- Visibility
- Approximate Size
- Callers
- Callees

## Property

Represents a property declaration.

## Field

Represents a field declaration.

## Reference

Represents a usage relationship between symbols.

## Dependency

Represents project and package dependencies.

## Call Graph

Captures caller/callee relationships between methods.

## Test

Represents a discovered test and the production symbols it exercises where available.

## Configuration

Represents build and project configuration files.

## Diagnostic

Represents build, analyzer, or test diagnostics associated with indexed artifacts.

# Indexing Requirements

The index shall:

- Preserve symbol identity.
- Support incremental updates.
- Support cross-reference queries.
- Avoid storing full source unless explicitly required.
- Be queryable without an LLM.

# Query Support

The index shall support retrieval by:

- Symbol
- File
- Namespace
- Type
- Method
- Dependency
- Reference
- Test
- Text search

# Non-Goals

The MVP does not require:

- Semantic embeddings
- Vector databases
- Git history
- Blame information
- AI-generated summaries

These capabilities may be added in future revisions.

# Relationship to DevX

The Repository Index is the authoritative data source for protocol operations such as:

- SEARCH_SYMBOL
- SEARCH_TEXT
- GET_FILE_SNAPSHOT
- GET_TYPE
- GET_METHOD_SUMMARY
- GET_CALLERS
- GET_CALLEES
- GET_REFERENCES
- GET_TESTS

The index supplies metadata only. Source code is returned only through explicit implementation expansion requests.

# Future Extensions

Potential future additions include:

- Git history
- Ownership metadata
- Semantic summaries
- Embeddings
- Incremental distributed indexing
- Multi-language repositories
