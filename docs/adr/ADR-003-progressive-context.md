# ADR-003: Progressive Context Retrieval

- Status: Accepted
- Date: 2026-07-02

## Context

Most engineering tasks require only a fraction of a repository.

## Decision

Context shall be retrieved incrementally through protocol operations, beginning with metadata and expanding to implementations only when requested.

## Consequences

- Smaller context windows
- Explicit expansion
- Lower inference cost
