# SPEC-003: Session Manager

- Status: Draft
- Version: 1.0

# Purpose

The Session Manager owns all mutable engineering state. The LLM is treated as stateless.

# Responsibilities

- Maintain the working set
- Track expanded files and symbols
- Record searches
- Store diagnostics
- Track applied patches
- Track build and test results
- Cache retrieved context

# Working Set

Contains only the context required for the current engineering task.

# Session Lifecycle

1. Create session
2. Execute protocol requests
3. Update working set
4. Apply patches
5. Run build/tests
6. Persist session
7. End session

# Persistence

Sessions should be resumable and reproducible.

# Non-Goals

- Repository indexing
- LLM reasoning
- Source editing
