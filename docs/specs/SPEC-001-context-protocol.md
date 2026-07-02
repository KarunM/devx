# SPEC-001: Engineering Context Protocol

- **Status:** Draft
- **Version:** 0.1.0
- **Date:** 2026-07-02
- **Owner:** ECP Project
- **Related ADRs:** ADR-001

---

## 1. Purpose

The Engineering Context Protocol (ECP) defines how a Large Language Model interacts with a source code repository through a local orchestration CLI.

The protocol exists to prevent the LLM from receiving an entire repository by default.

Instead, the LLM requests progressively richer repository context through explicit structured operations.

The CLI performs repository navigation.

The LLM performs reasoning.

---

## 2. Scope

This specification defines:

- protocol actors
- session lifecycle
- context levels
- request and response categories
- working set behavior
- diagnostics flow
- patch flow
- build and test flow
- protocol errors
- versioning expectations

This specification does not define:

- database schema
- parser implementation
- CLI command syntax
- LLM provider APIs
- language-specific parsing rules
- patch application internals
- UI behavior

Those concerns are defined in separate specifications.

---

## 3. Protocol Goals

ECP must:

1. Minimize token usage.
2. Make repository context retrieval explicit.
3. Allow progressive context expansion.
4. Keep the LLM stateless.
5. Keep repository state local.
6. Support multiple LLM providers.
7. Support multiple programming languages over time.
8. Prefer structural context over raw source text.
9. Make context requests reproducible.
10. Support deterministic build, test, diagnostic, and repair loops.

---

## 4. Non-Goals

ECP is not:

- a coding assistant product
- an IDE
- a model provider
- a vector database
- a replacement for language servers
- a free-form file editing protocol
- a prompt template format

ECP may integrate with these systems, but it is not defined by them.

---

## 5. Actors

### 5.1 User

The developer initiating an engineering task.

The user communicates with the CLI, not directly with the repository index.

### 5.2 CLI

The local orchestration layer.

The CLI owns:

- repository index access
- session state
- working set state
- protocol request handling
- patch application
- build execution
- test execution
- diagnostics collection
- LLM adapter coordination

### 5.3 Repository Index

A local structured representation of the repository.

The repository index exposes symbols, files, relationships, references, tests, and metadata.

### 5.4 LLM Adapter

A provider-specific integration layer.

The adapter translates between ECP messages and the selected model provider.

### 5.5 LLM

The reasoning engine.

The LLM must request repository context through protocol operations.

The LLM must not assume it has access to the entire repository.

---

## 6. Core Model

The repository is exposed as a structured API.

The LLM does not browse files directly.

The LLM asks the CLI for specific information.

The CLI returns the smallest useful response for the requested context level.

Implementation source code is only returned when explicitly requested.

---

## 7. Session Lifecycle

A typical ECP session follows this lifecycle:

```text
Start Session
    ↓
Load Repository Index
    ↓
Receive User Task
    ↓
LLM Requests Context
    ↓
CLI Returns Structured Context
    ↓
LLM Requests More Context or Produces Patch
    ↓
CLI Applies Patch
    ↓
CLI Runs Build or Tests
    ↓
CLI Returns Diagnostics
    ↓
LLM Repairs if Necessary
    ↓
Complete Session
```

The CLI persists session state during the session.

The LLM receives only the current working set and requested responses.

---

## 8. Working Set

The working set is the subset of repository context currently visible to the LLM.

The CLI owns the working set.

The working set may contain:

- search results
- file snapshots
- type snapshots
- method summaries
- expanded method bodies
- diagnostics
- build output
- test output
- patch history

The LLM may request additions to the working set.

The CLI may summarize, compact, or evict working set items according to session policy.

The CLI must preserve enough metadata to make prior context requests reproducible.

---

## 9. Context Levels

ECP exposes repository context through progressive levels.

### 9.1 Level 0 — Search

Search returns matching repository entities.

No source code is returned.

Example operations:

```text
SEARCH_SYMBOL
SEARCH_TEXT
```

Example response shape:

```json
{
  "level": 0,
  "operation": "SEARCH_SYMBOL",
  "query": "ExecutionGraph",
  "results": [
    {
      "kind": "type",
      "name": "ExecutionGraph",
      "file": "src/Core/ExecutionGraph.cs"
    }
  ]
}
```

### 9.2 Level 1 — File Snapshot

A file snapshot describes the structure of a file without implementation bodies.

Example operation:

```text
GET_FILE_SNAPSHOT
```

A file snapshot may include:

- file path
- approximate line count
- namespaces
- types
- methods
- properties
- dependencies
- references
- tests

### 9.3 Level 2 — Symbol Snapshot

A symbol snapshot describes a type, method, property, constructor, interface, enum, or field without implementation bodies.

Example operations:

```text
GET_TYPE
GET_METHOD_SUMMARY
```

A method summary may include:

- signature
- parameters
- return type
- modifiers
- containing type
- references
- callers
- callees
- approximate size
- diagnostics associated with the symbol

### 9.4 Level 3 — Implementation Expansion

Implementation expansion returns the implementation body of exactly one requested symbol.

Example operation:

```text
GET_METHOD
```

The response must not include unrelated methods or entire files unless explicitly requested by a higher-level operation.

### 9.5 Level 4 — Related Expansion

Related expansion returns repository relationships.

Example operations:

```text
GET_CALLERS
GET_CALLEES
GET_REFERENCES
GET_IMPLEMENTATIONS
GET_TESTS
```

Related expansion may return summaries, symbol identifiers, or source snippets depending on the operation and request parameters.

---

## 10. Protocol Operations

Protocol operations are structured requests made by the LLM and fulfilled by the CLI.

Each request must include:

- operation name
- protocol version
- request id
- parameters

Each response must include:

- request id
- operation name
- status
- result or error
- context level
- metadata

---

## 11. Search Operations

### 11.1 SEARCH_SYMBOL

Searches indexed symbols by name.

Input:

```json
{
  "operation": "SEARCH_SYMBOL",
  "query": "Authentication"
}
```

Output:

```json
{
  "status": "ok",
  "results": [
    {
      "symbolId": "type:src/Auth/AuthenticationExtensions.cs:AuthenticationExtensions",
      "kind": "class",
      "name": "AuthenticationExtensions",
      "file": "src/Auth/AuthenticationExtensions.cs"
    }
  ]
}
```

### 11.2 SEARCH_TEXT

Searches repository text or indexed text fields.

SEARCH_TEXT may return file paths, symbols, or text locations.

Source code snippets should be omitted by default unless explicitly requested.

---

## 12. Snapshot Operations

### 12.1 GET_FILE_SNAPSHOT

Returns structural metadata for one file.

Input:

```json
{
  "operation": "GET_FILE_SNAPSHOT",
  "file": "src/Auth/AuthenticationExtensions.cs"
}
```

### 12.2 GET_TYPE

Returns structural metadata for one type.

Input:

```json
{
  "operation": "GET_TYPE",
  "symbolId": "type:src/Auth/AuthenticationExtensions.cs:AuthenticationExtensions"
}
```

### 12.3 GET_METHOD_SUMMARY

Returns structural metadata for one method without implementation.

Input:

```json
{
  "operation": "GET_METHOD_SUMMARY",
  "symbolId": "method:src/Auth/AuthenticationExtensions.cs:AuthenticationExtensions.ConfigureAuthentication"
}
```

---

## 13. Expansion Operations

### 13.1 GET_METHOD

Returns the implementation of a single method.

The response must include enough surrounding metadata to identify the method safely.

The response must not include unrelated method bodies.

### 13.2 GET_CALLERS

Returns symbols that call the requested symbol.

### 13.3 GET_CALLEES

Returns symbols called by the requested symbol.

### 13.4 GET_REFERENCES

Returns references to the requested symbol.

### 13.5 GET_IMPLEMENTATIONS

Returns implementations of an interface, abstract member, or contract-like symbol.

### 13.6 GET_TESTS

Returns tests related to a file, type, method, or feature.

---

## 14. Patch Operations

Patches are structured operations.

The LLM must not directly rewrite arbitrary files.

Patch requests may include operations such as:

```text
CREATE_FILE
DELETE_FILE
REPLACE_METHOD
INSERT_METHOD
DELETE_METHOD
REPLACE_TYPE
INSERT_TYPE
DELETE_TYPE
REPLACE_BLOCK
```

Patch operations must include preconditions where possible.

Example:

```json
{
  "operation": "PATCH",
  "patches": [
    {
      "kind": "REPLACE_METHOD",
      "targetSymbolId": "method:src/Auth/AuthExtensions.cs:AuthExtensions.ConfigureAuthentication",
      "expectedHash": "sha256:...",
      "replacement": "..."
    }
  ]
}
```

The CLI validates patch operations before applying them.

Patch application may fail if:

- the target symbol does not exist
- the target hash does not match
- the replacement is syntactically invalid
- the patch conflicts with current repository state
- the operation violates language-specific constraints

---

## 15. Build Operations

### 15.1 RUN_BUILD

Runs the configured build command.

The CLI returns:

- build status
- exit code
- diagnostics
- relevant file locations
- summarized output
- full output reference if available

The CLI should avoid sending excessive build logs to the LLM by default.

---

## 16. Test Operations

### 16.1 RUN_TESTS

Runs tests.

The request may target:

- entire repository
- project
- test file
- test class
- test method
- tests related to a symbol

The CLI returns:

- test status
- passed count
- failed count
- skipped count
- failure diagnostics
- relevant stack traces
- summarized output

---

## 17. Diagnostics Operations

### 17.1 GET_DIAGNOSTICS

Returns current diagnostics from build, test, parser, or analyzer output.

Diagnostics should be structured.

Example:

```json
{
  "operation": "GET_DIAGNOSTICS",
  "results": [
    {
      "severity": "error",
      "code": "CS0246",
      "message": "The type or namespace name could not be found",
      "file": "src/Auth/AuthExtensions.cs",
      "line": 42,
      "column": 17
    }
  ]
}
```

---

## 18. Error Handling

Every failed operation must return a structured error.

Example:

```json
{
  "status": "error",
  "error": {
    "code": "SYMBOL_NOT_FOUND",
    "message": "No symbol matched the requested identifier.",
    "recoverable": true
  }
}
```

Common error codes:

```text
INVALID_REQUEST
UNSUPPORTED_OPERATION
INDEX_NOT_FOUND
INDEX_STALE
SYMBOL_NOT_FOUND
FILE_NOT_FOUND
AMBIGUOUS_SYMBOL
PATCH_CONFLICT
PATCH_VALIDATION_FAILED
BUILD_FAILED
TEST_FAILED
ADAPTER_ERROR
INTERNAL_ERROR
```

Recoverable errors should include suggested next operations where possible.

---

## 19. Protocol Metadata

Responses may include metadata such as:

```json
{
  "metadata": {
    "protocolVersion": "0.1.0",
    "indexVersion": "0.1.0",
    "repositoryRoot": "...",
    "language": "csharp",
    "generatedAt": "2026-07-02T12:00:00Z",
    "tokenEstimate": 420
  }
}
```

Metadata helps the CLI and LLM reason about freshness, size, and compatibility.

---

## 20. Versioning

ECP uses semantic versioning.

Protocol messages must include a protocol version.

Breaking changes require a major version increment.

Backward-compatible additions require a minor version increment.

Clarifications and bug fixes require a patch version increment.

Adapters must declare supported protocol versions.

---

## 21. Security and Privacy

Repository content remains local unless explicitly transmitted to an LLM provider through an adapter.

The CLI should make transmitted context auditable.

The CLI should avoid sending:

- unrelated source files
- secrets
- environment files
- private keys
- credentials
- large raw logs

Future specifications may define redaction policies and secret scanning behavior.

---

## 22. MVP Requirements

The MVP must support:

- C# / .NET repositories
- local indexing
- SQLite-backed index storage
- symbol search
- file snapshots
- type snapshots
- method summaries
- method expansion
- structured patches
- build execution
- test execution
- diagnostics return
- at least one LLM adapter

Embeddings are not required for the MVP.

---

## 23. Open Questions

The following questions remain unresolved:

1. Should protocol messages be JSON, YAML, or another structured format?
2. Should the working set be exposed as a first-class protocol object?
3. Should token budgets be enforced by the CLI or suggested to the LLM?
4. How should secret detection and redaction be standardized?
5. How should protocol compatibility be negotiated with adapters?
6. Should ECP support streaming responses?
7. How should multi-repository workspaces be represented?

---

## 24. Related Specifications

Expected follow-up specifications:

- SPEC-002: Repository Index
- SPEC-003: Session Management
- SPEC-004: Patch Model
- SPEC-005: CLI Commands
- SPEC-006: LLM Adapter Interface
- SPEC-007: C# Parser Pipeline
- SPEC-008: Storage Model

---

## 25. Summary

The Engineering Context Protocol defines a structured interaction model between an LLM and a source code repository.

The protocol ensures that repository context is retrieved progressively, explicitly, and reproducibly.

The CLI owns repository navigation and session state.

The LLM owns reasoning.

This separation allows ECP to minimize token usage, reduce repeated repository discovery, and provide a model-agnostic foundation for AI-assisted software engineering.
