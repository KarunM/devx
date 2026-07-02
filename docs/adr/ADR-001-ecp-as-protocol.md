# ADR-001: ECP as a Protocol, Not a Coding Assistant

- Status: Accepted
- Date: 2026-07-02

## Context

Most AI coding tools tightly couple repository discovery, context management, and reasoning. This limits transparency, portability, and developer control.

## Decision

ECP will be developed as a protocol and orchestration layer rather than a coding assistant.

The CLI owns repository discovery, context retrieval, session management, patch application, and engineering workflows.

LLMs consume the protocol and perform reasoning.

## Consequences

### Positive

- Model agnostic
- Reproducible engineering sessions
- Explicit context retrieval
- Lower token usage
- Reusable across IDEs and providers

### Negative

- Requires local infrastructure
- Requires protocol implementations
