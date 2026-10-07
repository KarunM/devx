# SPEC-012: DevX-Driven EverCode Dogfooding Workflow

**Status:** Draft  
**Area:** DevX + EverCode Integration  
**Purpose:** Use DevX as the primary protocol for AI agents building EverCode.

---

## Objective

Define the workflow where Codex, Copilot, and other AI coding agents use DevX to build and maintain EverCode.

EverCode is the enterprise application platform.  
DevX is the engineering protocol that gives agents controlled access to repository discovery, source reading, patching, build, test, diagnostics, and reporting.

This spec exists to ensure DevX evolves from real EverCode development needs instead of speculative protocol design.

---

## Core Principle

DevX work should be driven by EverCode dogfooding.

When an AI agent is building EverCode:

- If the issue is an EverCode product/platform bug, fix EverCode.
- If the issue is caused by missing protocol capability, improve DevX.
- If the issue is caused by poor agent behavior, improve the DevX-first workflow prompt.

---

## Intended Architecture

```text
AI Agent
  ↓
DevX CLI / Protocol
  ↓
EverCode Repository
  ↓
EverCode Platform
  ↓
Enterprise Applications
```

Agents should interact with EverCode through DevX wherever possible.

---

## Required Agent Workflow

For every EverCode task, the agent should:

1. Read the task/spec directly.
2. Check repository index state.

```bash
devx index status --format json
```

3. Reuse or create a session.

```bash
devx session create --format json
```

4. Discover relevant code using DevX.

```bash
devx search symbol <query> --session <id> --format json
devx search text <query> --session <id> --format json
```

5. Read source through DevX.

```bash
devx read method --id <method-id> --session <id> --format json
devx read type --id <type-id> --session <id> --format json
devx read file <path> --range <start>:<end> --session <id> --format json
```

6. Prefer structured DevX patches for edits.

```bash
devx patch preview <patch-file> --session <id> --format json
devx patch validate <patch-file> --session <id> --format json
devx patch apply <patch-file> --session <id> --format json
```

7. Validate with DevX.

```bash
devx build --session <id> --format json
devx test --session <id> --format json
devx report --session <id> --format json
```

---

## Fallback Rules

Direct source reads are allowed only when DevX cannot provide the needed source context.

Direct source edits are allowed only when DevX cannot express the required patch.

Every fallback must be reported with:

- what was done directly
- why DevX was insufficient
- whether this should become a future DevX capability

---

## Dogfooding Output Requirements

At the end of every EverCode task, the agent must report:

- EverCode files modified
- DevX commands used
- direct reads/edits performed
- validation commands
- DevX report output
- DevX limitations encountered
- proposed DevX follow-up tasks, if any

---

## DevX Limitation Categories

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

- EverCode metadata not visible to DevX
- application modules not indexed
- database schema not exposed
- migrations not represented

### Workflow Limitation

Examples:

- agent ignored DevX
- agent used direct file reads too early
- agent created too much noise
- agent failed to include report output

---

## Backlog Rule

Do not add new DevX protocol features unless one of these is true:

1. An EverCode task exposed a concrete limitation.
2. Multiple agents repeatedly failed due to the same missing capability.
3. A missing capability blocks DevX-first development.

This keeps DevX lean and product-driven.

---

## Success Criteria

This workflow succeeds when:

- Codex/Copilot can complete EverCode tasks mostly through DevX.
- Direct source reads are rare and justified.
- Direct edits are rare and justified.
- DevX reports show useful efficiency metrics.
- DevX limitations are captured as actionable follow-up specs.
- EverCode progress drives DevX evolution.

---

## Initial Dogfooding Experiment

Pick one small EverCode task.

The task should be real but low risk, such as:

- fix a small bug
- add a small CLI/API option
- update one validation rule
- add one test-backed behavior

Run the agent with the DevX-first workflow.

After completion, classify the outcome:

```text
EverCode task completed: yes/no
DevX was sufficient: yes/no
Direct reads used: count
Direct edits used: count
Patch operations used: count
Build/test passed: yes/no
DevX limitations found: list
```

---

## Definition of Done

DevX becomes the standard protocol that AI agents use to build EverCode, and every missing DevX capability is discovered through real EverCode development rather than guessed in advance.
