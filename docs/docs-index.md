# ECP Documentation Index

> Engineering Context Protocol (ECP) Documentation Guide

## Purpose

This document is the entry point for all project documentation. It describes how the documentation is organized and the recommended reading order for contributors, maintainers, and AI systems.

## Documentation Hierarchy

```text
Vision
    ↓
Architecture Decisions (ADRs)
    ↓
Specifications
    ↓
Implementation Plans
    ↓
Tasks
    ↓
Source Code
```

## Reading Order

### 1. Vision

`docs/vision.md`

Read first to understand the project's purpose, mission, principles, scope, and long-term goals.

### 2. Architecture Decision Records

`docs/adr/`

- ADR-001 — ECP as a Protocol, Not a Coding Assistant
- ADR-002 — Local Repository Discovery
- ADR-003 — Progressive Context Retrieval
- ADR-004 — CLI-Owned Session State
- ADR-005 — Structured Patch Model

These explain **why** major architectural decisions were made.

### 3. Specifications

`docs/specs/`

- SPEC-001 — Context Protocol
- SPEC-002 — Repository Index
- SPEC-003 — Session Manager
- SPEC-004 — Patch Model
- SPEC-005 — CLI Specification

These define **what** the system does and the contracts it exposes.

### 4. Implementation Plans

`docs/plans/`

- PLAN-001 — Indexing MVP
- PLAN-002 — Roslyn Indexer
- PLAN-003 — Context Protocol Engine
- PLAN-004 — Session Manager
- PLAN-005 — Patch Engine

These organize implementation into major workstreams.

### 5. Tasks

`docs/tasks/`

Tasks are the executable units of work. Each task should contain:

- Objective
- Prerequisites
- Inputs
- Scope
- Implementation Steps
- Acceptance Criteria
- Verification Steps
- Definition of Done

## Supporting Documents

- `docs/glossary.md`
- `docs/repository-layout.md`
- `docs/roadmap.md`

## Documentation Principles

1. Vision explains why the project exists.
2. ADRs record architectural decisions.
3. Specifications define behavior.
4. Plans organize implementation.
5. Tasks drive execution.
6. Code implements the documented architecture.

## Recommended Workflow

```text
vision.md
    ↓
Relevant ADRs
    ↓
Relevant Specification
    ↓
Current Plan
    ↓
Current Task
    ↓
Implement Code
```

## AI-Assisted Development

The documentation is designed for progressive context retrieval. An AI should typically load only:

- the relevant specification,
- the active implementation plan,
- the current task,

rather than the entire documentation set.

This mirrors the philosophy of the Engineering Context Protocol itself.

## Versioning

- Vision changes rarely.
- ADRs are effectively immutable once accepted.
- Specifications evolve with the protocol.
- Plans evolve with implementation.
- Tasks are completed and archived.
