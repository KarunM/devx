# Architecture Overview

> Engineering Context Protocol (ECP)

## Purpose

This document provides a high-level view of the ECP architecture and explains how the major components interact. It is intended as the architectural map of the system and complements the Vision, ADRs, Specifications, Plans, and Tasks.

---

# System Overview

```text
                User
                  │
                  ▼
             ECP CLI
                  │
        ┌─────────┴─────────┐
        │                   │
        ▼                   ▼
 Session Manager      Protocol Engine
        │                   │
        └─────────┬─────────┘
                  ▼
          Repository Index
                  │
      ┌───────────┴───────────┐
      ▼                       ▼
 SQLite Storage        Language Parsers
                               │
                               ▼
                           Roslyn (.NET)

                  │
                  ▼
             Patch Engine
                  │
                  ▼
         Build / Test Runner
                  │
                  ▼
            Diagnostics

                  │
                  ▼
             LLM Adapter
                  │
                  ▼
        OpenAI / Anthropic / Ollama /
        Gemini / DeepSeek / Local
```

---

# Component Responsibilities

## CLI

The CLI is the primary user interface.

Responsibilities:

- Execute commands
- Manage sessions
- Invoke protocol operations
- Display results
- Coordinate engineering workflows

---

## Session Manager

Owns all mutable engineering state.

Tracks:

- Working set
- Expanded symbols
- Search history
- Patch history
- Build results
- Test results
- Diagnostics

The LLM remains stateless.

---

## Protocol Engine

Implements the Engineering Context Protocol.

Responsibilities:

- Validate requests
- Route operations
- Return structured responses
- Enforce context levels

Representative operations:

- SEARCH_SYMBOL
- GET_FILE_SNAPSHOT
- GET_METHOD
- PATCH
- RUN_BUILD
- RUN_TESTS

---

## Repository Index

Provides the canonical structural view of the repository.

Contains:

- Projects
- Files
- Namespaces
- Types
- Methods
- References
- Call graph
- Tests
- Diagnostics

The index exposes metadata first and source code only through explicit expansion requests.

---

## Storage

SQLite is the MVP persistence layer.

Stores:

- Repository metadata
- Structural index
- Session state
- Diagnostics

The storage layer is replaceable behind abstractions.

---

## Language Parsers

Language-specific parsers populate the repository index.

Initial implementation:

- C# using Roslyn

Future implementations:

- TypeScript
- Java
- Go
- Python
- Rust

---

## Patch Engine

Applies validated structured patch operations.

Responsibilities:

- Validate
- Apply
- Roll back (future)
- Preserve repository consistency

---

## Build & Test Runner

Executes project builds and automated tests.

Produces structured diagnostics consumed by the protocol.

---

## LLM Adapter

Translates protocol operations into provider-specific API calls.

Supported providers may include:

- OpenAI
- Anthropic
- Gemini
- Ollama
- DeepSeek
- Local inference servers

The protocol remains provider independent.

---

# Engineering Workflow

```text
User Request
      │
      ▼
SEARCH_SYMBOL
      ▼
GET_FILE_SNAPSHOT
      ▼
GET_METHOD_SUMMARY
      ▼
GET_METHOD
      ▼
PATCH
      ▼
RUN_BUILD
      ▼
GET_DIAGNOSTICS
      ▼
RUN_TESTS
      ▼
Repeat Until Successful
```

---

# Layered Architecture

```text
Vision
    ↓
ADRs
    ↓
Specifications
    ↓
Plans
    ↓
Tasks
    ↓
Implementation
```

Each layer refines the one above it without replacing it.

---

# Design Principles

1. Repository discovery happens locally.
2. The repository behaves like an API, not a collection of files.
3. Context is expanded progressively.
4. Session state belongs to the CLI.
5. LLMs perform reasoning, not repository navigation.
6. Patches are structured and validated.
7. The protocol is deterministic and reproducible.
8. The architecture is provider and language agnostic.

---

# Future Architecture

Potential future capabilities include:

- Incremental indexing
- Distributed indexing
- Semantic summaries
- Optional embeddings
- Remote repository support
- IDE integrations
- Protocol version negotiation

---

# Summary

ECP separates deterministic engineering concerns from probabilistic reasoning.

The CLI owns orchestration.

The repository is represented as a structured engineering model.

The LLM requests only the context it needs through explicit protocol operations, enabling efficient, reproducible, and model-agnostic AI-assisted software engineering.
