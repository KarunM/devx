# ADR-002: Repository Discovery is Performed Locally

- Status: Accepted
- Date: 2026-07-02

## Context

Repository exploration is deterministic work and should not consume LLM context.

## Decision

Repositories shall be indexed locally and queried through the DevX protocol. LLMs request information from the index rather than searching raw files.

## Consequences

- Deterministic discovery
- Reduced prompt size
- Faster repeated interactions
