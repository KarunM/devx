# SPEC-013: ECP-Orchestrated Ask Workflow

**Status:** Draft  
**Area:** ECP Ask / Local LLM Orchestration  
**Purpose:** Replace free-form tool-calling ask behavior with an ECP-managed coding workflow where the LLM is used only for requirement understanding, incremental context requests, code generation, and repair suggestions.

---

## Objective

`ecp ask` should become an ECP-owned orchestration loop.

The LLM must not be responsible for deciding the development workflow.

ECP owns:

- discovery
- source reading
- patch creation/application
- build
- test
- diagnostics
- report
- completion decision

The LLM owns:

- understanding the requirement
- requesting more context when needed
- proposing code changes
- repairing build/test failures
- explaining blockers when it cannot produce a safe change

---

## Problem

The current `ask` mode exposes low-level ECP tools to the LLM.

This causes small local models to:

- search repeatedly
- read files repeatedly
- invent unsupported tools
- attempt patch/build/test commands directly
- prematurely claim completion
- loop until max turns

A 7B/8B local model should not be expected to act as a complete autonomous coding agent.

Instead, ECP should act as the deterministic engineering orchestrator.

---

## New Mental Model

### Old Model

```text
User goal
  ↓
LLM chooses tools
  ↓
LLM searches
  ↓
LLM reads
  ↓
LLM patches
  ↓
LLM builds/tests
  ↓
LLM says done
```

### New Model

```text
User goal
  ↓
ECP discovers context
  ↓
ECP asks LLM for a code change
  ↓
ECP creates/applies patch
  ↓
ECP builds/tests
  ↓
If failure:
    ECP gives LLM compact failure context
    LLM proposes fix
    ECP applies fix
  ↓
ECP reports completion
```

---

## Core Principle

The LLM does not call ECP tools.

The LLM receives compact, task-specific context and returns structured code-change instructions.

ECP executes the workflow.

---

## Ask Workflow

### 1. Start

User runs:

```bash
ecp ask "can you create a health command on cli tool" --session <id> --format json
```

ECP records:

- primary goal
- session id
- repository id
- current index status

---

### 2. Preflight

ECP performs deterministic preflight:

```bash
ecp index status
ecp capabilities
```

If index is missing or stale, ECP may update/index before involving the LLM.

---

### 3. Discovery

ECP performs repository discovery itself.

Examples:

- search symbols related to the goal
- search text related to the goal
- identify likely files
- read full files or ranges as needed
- prefer method/type reads when useful

ECP does not yet ask the LLM for a change proposal at this stage. Discovery is ECP-owned and only informs the initial context bundle.

The LLM does not choose search/read tools directly.

---

### 4. Context Assembly

ECP builds a compact prompt for the LLM.

Prompt contains:

```text
Primary goal:
<user requirement>

Relevant files:
- <path>
- <path>

Current source context:
<selected source snippets or full files>

Instructions:
Return a structured change proposal.
Do not describe ECP tool calls.
Do not claim completion.

The initial prompt also includes the response contract and the allowed response kinds.
```

Full file reads are allowed here when ECP determines they are useful.

Token cost is accepted as a local inference tradeoff; ECP controls how much context is sent.

---

### 5. LLM Context Loop

The LLM may request more context before it proposes a change.

Allowed response kinds:

- `context_request`
- `change_proposal`
- `fix_proposal`
- `no_change`
- `blocker`

#### `context_request` schema

```json
{
  "kind": "context_request",
  "reason": "...",
  "requests": [
    { "type": "search_symbol", "query": "..." },
    { "type": "search_text", "query": "..." },
    { "type": "read_file", "path": "...", "range": "start:end" },
    { "type": "read_method", "id": "..." },
    { "type": "read_type", "id": "..." }
  ]
}
```

Rules:

- The LLM can request context.
- ECP executes context requests.
- ECP summarizes results and sends them back.
- The LLM cannot call patch, build, test, or report tools.
- ECP decides patch, build, test, and report.
- Completion is still ECP-owned.
- Context grows incrementally with:
  - primary goal
  - previous context results
  - applied patches
  - build/test diagnostics
  - current requested context

#### Loop

1. ECP sends an initial prompt with the goal and response schema.
2. If the LLM returns `context_request`, ECP executes the requested searches and reads.
3. ECP appends compact context results.
4. ECP asks the LLM again.
5. Repeat until the LLM returns `change_proposal`, `fix_proposal`, `no_change`, or `blocker`.
6. Then ECP handles patch, build, test, and report.

### 6. LLM Change Proposal

The LLM returns a structured response.

Example:

```json
{
  "kind": "change_proposal",
  "summary": "Add a health command to the CLI.",
  "changes": [
    {
      "path": "src/Ecp.Cli/Program.cs",
      "operation": "ReplaceMethod",
      "target": "RunAsync",
      "replacement": "..."
    }
  ],
  "testsToAddOrUpdate": [],
  "notes": []
}
```

The LLM does not call patch tools.

---

### 7. Patch Construction

ECP converts the LLM change proposal into an ECP patch file.

ECP then runs:

```bash
ecp patch preview <patch-file> --session <id> --format json
ecp patch validate <patch-file> --session <id> --format json
ecp patch apply <patch-file> --session <id> --format json
```

If patch construction fails, ECP asks the LLM for a corrected proposal using the failure context.

---

### 8. Build

After patch apply succeeds, ECP runs:

```bash
ecp build --session <id> --format json
```

If build fails, ECP constructs a repair prompt.

Repair prompt contains:

```text
Primary goal:
<original user goal>

Files changed:
<list>

Patch applied:
<summary or patch excerpt>

Build failed:
<compiler errors>

Current relevant source:
<affected files or snippets>

Task:
Return a fix proposal.
```

The LLM proposes a fix.

---

## Validation Expectations

For `ecp ask "can you add a health command to cli tool?" --debug-ask --format json`, the first LLM turn should ask for search/read context.

ECP should execute those requests, summarize the results, and re-query the LLM.

The proposal that follows should be grounded in real indexed code rather than a generic method replacement.

ECP applies the fix and rebuilds.

---

### 8. Test

After build succeeds, ECP runs:

```bash
ecp test --session <id> --format json
```

If tests fail, ECP constructs a test-repair prompt.

Repair prompt contains:

```text
Primary goal:
<original user goal>

Files changed:
<list>

Build status:
passed

Tests failed:
<test errors>

Current relevant source:
<affected files or snippets>

Task:
Return a fix proposal.
```

The LLM proposes a fix.

ECP applies the fix and retests.

---

### 9. Report

After build and tests succeed, ECP runs:

```bash
ecp report --session <id> --format json
```

ECP determines completion.

The LLM does not decide final completion.

---

## Completion Rule

`ecp ask` is complete only when ECP has verified:

- patch applied or explicit no-change decision accepted
- build succeeded
- tests succeeded
- report generated

The LLM cannot mark the task complete.

---

## No-Change Decision

Sometimes no code change is needed.

The LLM may return:

```json
{
  "kind": "no_change",
  "reason": "The requested behavior already exists.",
  "evidence": [
    "health command is already registered in Program.cs",
    "existing tests cover health output"
  ]
}
```

ECP must verify the evidence before accepting no-change.

For code-change prompts, no-change is accepted only if ECP confirms it through discovery and validation.

---

## LLM Response Types

### Change Proposal

```json
{
  "kind": "change_proposal",
  "summary": "...",
  "changes": [],
  "testsToAddOrUpdate": [],
  "notes": []
}
```

### Fix Proposal

```json
{
  "kind": "fix_proposal",
  "summary": "...",
  "changes": [],
  "reason": "..."
}
```

### Blocker

```json
{
  "kind": "blocker",
  "reason": "...",
  "neededContext": [],
  "suggestedNextStep": "..."
}
```

### No Change

```json
{
  "kind": "no_change",
  "reason": "...",
  "evidence": []
}
```

Free-form text is invalid.

---

## Prompt Growth Model

Context should grow incrementally.

### Initial Prompt

```text
Primary goal:
Create a health command.

Relevant source:
Program.cs
Command registration area
```

### Build Failure Prompt

```text
Primary goal:
Create a health command.

Patch applied:
Added health command handler.

Build failed:
CS0103: The name HealthCommand does not exist.

Current source:
Program.cs
```

### Test Failure Prompt

```text
Primary goal:
Create a health command.

Patch applied:
Added health command.

Build passed.

Tests failed:
Expected "OK", actual "Healthy".

Current source:
Program.cs
HealthCommandTests.cs
```

This keeps the LLM focused on the current issue while preserving the original goal.

---

## What the LLM Must Not Do

The LLM must not:

- choose ECP tools
- call search/read/patch/build/test/report
- decide completion
- invent tool names
- directly write files
- claim success without validation
- request unnecessary repository-wide context

---

## What ECP Must Do

ECP must:

- discover context
- decide what source to read
- allow full file reads when useful
- construct patches
- preview/validate/apply patches
- run build
- run tests
- collect diagnostics
- report results
- decide completion
- ask the LLM only focused questions

---

## Logging Requirements

`ecp ask` should log each deterministic ECP step.

Example:

```text
[Preflight] index status: Current
[Discover] search_symbol query=health symbols=2
[Read] file=src/Ecp.Cli/Program.cs lines=1-240
[LLM] request kind=change_proposal promptTokensApprox=1200
[LLM] response kind=change_proposal changes=1
[Patch] created path=.ecp/sessions/<id>/patches/001.patch.json operations=1
[Patch] preview success
[Patch] validate success
[Patch] apply success
[Build] success
[Test] success passed=29 failed=0
[Report] success
[Done] completed
```

No silent long-running phases.

For long operations, print heartbeat logs.

---

## Error Handling

If the LLM returns invalid JSON:

- ECP sends a correction prompt.
- Include the expected schema.
- Include the invalid response summary.
- Limit retries.

If patch apply fails:

- ECP sends patch failure context to the LLM.
- LLM returns a fix proposal.
- ECP constructs and applies a new patch.

If build fails:

- ECP sends compiler errors and changed source to the LLM.
- LLM returns a fix proposal.

If tests fail:

- ECP sends test failures and changed source to the LLM.
- LLM returns a fix proposal.

If retries are exhausted:

```json
{
  "success": false,
  "error": {
    "code": "AskMaxRepairIterationsExceeded",
    "message": "...",
    "primaryGoal": "...",
    "lastStage": "...",
    "lastDiagnostics": []
  }
}
```

---

## Acceptance Criteria

`ecp ask "can you create a health command on cli tool"` should:

1. Run ECP-driven discovery.
2. Ask the LLM for a change proposal.
3. Create an ECP patch.
4. Preview the patch.
5. Validate the patch.
6. Apply the patch.
7. Build.
8. Test.
9. Report.
10. Return success only after ECP validation.

The LLM should never directly choose low-level tools.

---

## Implementation Plan

### Phase 1

Add an ECP-managed ask pipeline:

```text
Preflight
Discover
AssembleContext
RequestChangeProposal
CreatePatch
PreviewPatch
ValidatePatch
ApplyPatch
Build
Test
Report
Complete
```

### Phase 2

Add repair loops:

```text
PatchRepair
BuildRepair
TestRepair
```

### Phase 3

Improve context assembly:

- source ranking
- full file inclusion
- diff summaries
- diagnostic-focused context

### Phase 4

Retire low-level LLM tool-calling mode or keep it behind an experimental flag.

---

## Definition of Done

`ecp ask` becomes an ECP-driven coding workflow where:

- ECP owns all repository operations.
- The LLM only produces structured change/fix proposals.
- The prompt context grows with primary goal, patches applied, build/test diagnostics, and current source.
- Completion is decided by ECP after successful validation.
