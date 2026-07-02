# SPEC-012: ECP-Driven EverCode Dogfooding Workflow

**Status:** Draft  
**Area:** ECP + EverCode Integration  
**Purpose:** Use ECP as the primary protocol for AI agents building EverCode.

---

## Objective

Define the workflow where Codex, Copilot, and other AI coding agents use ECP to build and maintain EverCode.

EverCode is the enterprise application platform.  
ECP is the engineering protocol that gives agents controlled access to repository discovery, source reading, patching, build, test, diagnostics, and reporting.

This spec exists to ensure ECP evolves from real EverCode development needs instead of speculative protocol design.

---

## Core Principle

ECP work should be driven by EverCode dogfooding.

When an AI agent is building EverCode:

- If the issue is an EverCode product/platform bug, fix EverCode.
- If the issue is caused by missing protocol capability, improve ECP.
- If the issue is caused by poor agent behavior, improve the ECP-first workflow prompt.

---

## Intended Architecture

```text
AI Agent
  ↓
ECP CLI / Protocol
  ↓
EverCode Repository
  ↓
EverCode Platform
  ↓
Enterprise Applications
```

Agents should interact with EverCode through ECP wherever possible.

---

## Required Agent Workflow

For every EverCode task, the agent should:

1. Read the task/spec directly.
2. Check repository index state.

```bash
ecp index status --format json
```

3. Reuse or create a session.

```bash
ecp session create --format json
```

4. Discover relevant code using ECP.

```bash
ecp search symbol <query> --session <id> --format json
ecp search text <query> --session <id> --format json
```

5. Read source through ECP.

```bash
ecp read method --id <method-id> --session <id> --format json
ecp read type --id <type-id> --session <id> --format json
ecp read file <path> --range <start>:<end> --session <id> --format json
```

6. Prefer structured ECP patches for edits.

```bash
ecp patch preview <patch-file> --session <id> --format json
ecp patch validate <patch-file> --session <id> --format json
ecp patch apply <patch-file> --session <id> --format json
```

7. Validate with ECP.

```bash
ecp build --session <id> --format json
ecp test --session <id> --format json
ecp report --session <id> --format json
```

---

## Fallback Rules

Direct source reads are allowed only when ECP cannot provide the needed source context.

Direct source edits are allowed only when ECP cannot express the required patch.

Every fallback must be reported with:

- what was done directly
- why ECP was insufficient
- whether this should become a future ECP capability

---

## Dogfooding Output Requirements

At the end of every EverCode task, the agent must report:

- EverCode files modified
- ECP commands used
- direct reads/edits performed
- validation commands
- ECP report output
- ECP limitations encountered
- proposed ECP follow-up tasks, if any

---

## ECP Limitation Categories

When the agent encounters a limitation, classify it as one of:

### Read Limitation

Examples:

- needed namespace-level read
- needed project file read
- needed generated metadata
- needed larger source window

### Patch Limitation

Examples:

- needed InsertClass
- needed ReplaceProperty
- needed AddAttribute
- needed multi-file transaction
- needed migration patch

### Validation Limitation

Examples:

- build command unavailable
- test result parsing missing
- diagnostics too shallow
- unable to detect affected tests

### Domain Limitation

Examples:

- EverCode metadata not visible to ECP
- application modules not indexed
- database schema not exposed
- migrations not represented

### Workflow Limitation

Examples:

- agent ignored ECP
- agent used direct file reads too early
- agent created too much noise
- agent failed to include report output

---

## Backlog Rule

Do not add new ECP protocol features unless one of these is true:

1. An EverCode task exposed a concrete limitation.
2. Multiple agents repeatedly failed due to the same missing capability.
3. A missing capability blocks ECP-first development.

This keeps ECP lean and product-driven.

---

## Success Criteria

This workflow succeeds when:

- Codex/Copilot can complete EverCode tasks mostly through ECP.
- Direct source reads are rare and justified.
- Direct edits are rare and justified.
- ECP reports show useful efficiency metrics.
- ECP limitations are captured as actionable follow-up specs.
- EverCode progress drives ECP evolution.

---

## Initial Dogfooding Experiment

Pick one small EverCode task.

The task should be real but low risk, such as:

- fix a small bug
- add a small CLI/API option
- update one validation rule
- add one test-backed behavior

Run the agent with the ECP-first workflow.

After completion, classify the outcome:

```text
EverCode task completed: yes/no
ECP was sufficient: yes/no
Direct reads used: count
Direct edits used: count
Patch operations used: count
Build/test passed: yes/no
ECP limitations found: list
```

---

## Definition of Done

ECP becomes the standard protocol that AI agents use to build EverCode, and every missing ECP capability is discovered through real EverCode development rather than guessed in advance.
