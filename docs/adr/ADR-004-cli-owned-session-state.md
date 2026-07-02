# ADR-004: The CLI Owns Session State

- Status: Accepted
- Date: 2026-07-02

## Context

LLMs are inherently stateless across requests.

## Decision

The CLI is the authoritative owner of engineering session state, including working sets, diagnostics, searches, patches, and build/test history.

## Consequences

- Resumable sessions
- Stateless model adapters
- Consistent workflows
