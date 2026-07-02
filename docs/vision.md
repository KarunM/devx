# Engineering Context Protocol (ECP)

> **Vision**

## Purpose

The Engineering Context Protocol (ECP) aims to establish an open, vendor-neutral standard for how AI systems interact with software repositories.

Rather than treating a repository as a collection of text files, ECP exposes it as a structured engineering system that can be queried progressively.

The protocol separates deterministic repository navigation from probabilistic language model reasoning.

---

# Vision

AI should reason about software—not waste tokens discovering it.

Repositories already contain rich structural information such as projects, namespaces, types, methods, dependencies, references, and tests. This information should be discovered locally and exposed through a consistent protocol, allowing language models to request only the context they require.

ECP enables repositories to behave like APIs instead of documents.

---

# Mission

Build an open-source CLI that serves as the orchestration layer between source code repositories and any Large Language Model.

The CLI owns:

- Repository indexing
- Context retrieval
- Session management
- Patch application
- Build and test execution
- Diagnostics

The LLM owns reasoning.

---

# Guiding Principles

1. Repository discovery happens locally.
2. LLMs request context instead of receiving repositories.
3. Context is expanded progressively.
4. Structural analysis is preferred over semantic inference.
5. Embeddings are optional, not foundational.
6. The LLM is stateless.
7. The CLI owns the engineering session.
8. Every context request is explicit and reproducible.
9. Code modifications are represented as structured patches.
10. The protocol is independent of language model providers and programming languages.

---

# Scope

The initial release focuses on:

- C# and .NET repositories
- Structural indexing
- SQLite-backed metadata
- Progressive context retrieval
- Structured patch application
- Build and test orchestration

Future versions may add:

- Additional language parsers
- Incremental indexing
- Git history
- Semantic summaries
- Embeddings
- Remote repositories
- Distributed indexing

---

# Long-Term Goal

Establish ECP as the standard protocol for AI-assisted software engineering.

Just as HTTP standardized communication between clients and servers, ECP seeks to standardize communication between engineering repositories and AI systems.

The protocol should allow any compliant repository, CLI, IDE, or language model to collaborate through explicit, deterministic, and token-efficient context retrieval.

---

# Success Criteria

An engineering task should be solvable without transmitting an entire repository to the language model.

Repository discovery should be deterministic, reproducible, and independent of the selected AI provider.

Developers should retain control over how context is discovered, expanded, and shared throughout an engineering session.
