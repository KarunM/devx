# ADR-005: Structured Patch Operations

- Status: Accepted
- Date: 2026-07-02

## Context

Free-form text edits are difficult to validate and reproduce.

## Decision

Code modifications shall be represented as structured patch operations that are validated before being applied.

## Consequences

- Deterministic edits
- Safer automation
- Easier tooling integration
