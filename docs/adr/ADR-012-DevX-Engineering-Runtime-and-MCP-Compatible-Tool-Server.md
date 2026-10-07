# ADR-012: DevX Engineering Runtime and MCP-Compatible Tool Server

**Status:** Complete  
**Date:** 2026-07-08

## Context

ADR-011 separates the Engineering Session Harness from host-specific behavior and defines `IEngineeringRuntime` as the abstraction for repository and engineering execution capabilities.

DevX currently provides many of these capabilities through its CLI:

- repository indexing
- semantic, text, and symbol search
- file, type, and method reads
- patch creation
- patch validation
- patch apply and rollback
- build
- test
- report
- sessions

However, DevX is currently used inconsistently by agents. Some agents fall back to direct repository search, direct edits, or direct build/test commands when DevX commands are missing, incomplete, or unclear.

DevX should become the reliable engineering runtime underneath EverCode, local hosts, IDE extensions, and future AI-assisted engineering tools.

## Problem

DevX is not yet formalized as a runtime contract.

Current gaps include:

- patch create, validate, and apply support is inconsistent
- web, Vitest, and Vite validation are not first-class
- runtime capabilities are not discoverable enough for agents
- health and doctor checks are missing
- MCP compatibility is not formally defined
- agents may guess command syntax instead of discovering capabilities
- runtime support differs by project type without clear negotiation

Without a formal runtime contract, Engineering Session hosts cannot safely depend on DevX as the execution substrate.

## Decision

Define DevX as the first implementation of the Engineering Runtime contract.

DevX remains a CLI and tool runtime, not an agent harness.

DevX will expose a stable capability model for:

- discovery
- reading
- patch lifecycle
- validation
- reporting
- health
- runtime capability negotiation

DevX will also provide an MCP-compatible server interface so external AI clients can use the same engineering capabilities through the Model Context Protocol.

## Implementation Outcome

ADR-012 is complete at the reference/runtime hardening level.

Completed scope includes:

- runtime capability descriptors and negotiation
- health and doctor surfaces
- patch lifecycle parity
- runtime-aware build and test targeting
- incremental indexing
- fallback metrics and reporting
- MCP reference-surface projection

ADR-012 does not include production MCP hardening. Authentication, multi-user hosting, remote deployment, and production operational concerns remain out of scope for this decision.

## Follow-Up Backlog

The following items were observed during closure validation and should be tracked as follow-up backlog rather than closure blockers:

- Build-runtime reliability follow-up: transient `MSB4166` child-node shutdown during `devx build --format json`
- Report/session correlation bug: fresh-session `devx report --format json` output did not yet reflect immediately preceding build/test activity in `BuildTestMetrics`

## Positioning

DevX is:

```text
Engineering Context and Execution Platform
```

DevX is not:

- an AI model
- a reasoning engine
- an agent harness
- a conversation UI
- an Engineering Session host

DevX is the engineering runtime used by those higher layers.

## Runtime Layers

```text
AI / Harness / IDE / Agent
        ↓
DevX CLI or DevX MCP Server
        ↓
DevX Engine
        ↓
Repository / Index / Patch / Build / Test / Report
```

## Capability Model

DevX should expose capabilities in a provider-neutral way.

### Discovery

- repository detection
- project detection
- workspace metadata
- language/runtime detection

### Search

- semantic search
- text search
- symbol search
- reference search

### Read

- file read
- range read
- type read
- method read
- snapshot read

### Patch

- create patch
- validate patch
- apply patch
- rollback patch
- multi-file patch
- method replacement
- file creation
- file deletion

### Validation

- build
- test
- focused test
- runtime-specific validation
- web/Vitest/Vite validation
- .NET validation
- future language/runtime validation

### Reporting

- session report
- patch report
- validation report
- diagnostics summary
- metrics

### Runtime Health

- health
- doctor
- capabilities
- project/runtime status

## New Commands

### `devx capabilities`

Returns supported commands, operation types, project runtimes, and examples.

```bash
devx capabilities --format json
```

### `devx health`

Answers: "Can DevX be used successfully right now?"

Checks:

- CLI availability
- workspace detection
- repository detection
- index status
- storage/database status
- provider configuration if applicable
- patch engine readiness
- build/test tool availability

```bash
devx health --format json
```

### `devx doctor`

Provides deeper diagnostics and remediation hints.

```bash
devx doctor
devx doctor --format json
```

### Runtime-aware build/test

```bash
devx build --runtime dotnet --project apps/api/ala-mip-control.sln
devx test --runtime dotnet --project apps/api/Ala.Mip.ControlApi.Tests/Ala.Mip.ControlApi.Tests.csproj --filter EngineeringSession
devx build --runtime web --project apps/web
devx test --runtime web --project apps/web --filter EngineeringSession
```

### Incremental indexing

```bash
devx index file <path>
devx index files <path1> <path2>
devx index changed
```

## Patch Lifecycle Requirements

Patch operations must be consistent across create, validate, apply, and rollback.

If `devx patch create` accepts an operation, then:

- `devx patch validate` must understand it
- `devx patch apply` must apply it
- `devx patch rollback` must roll it back where possible

Required patch operations:

- replace-file
- update-file
- replace-text
- create-file
- delete-file
- multi-file
- method-replace
- range-replace

Patch reports must include:

- patch id
- files changed
- operation types
- validation status
- apply status
- rollback availability
- diagnostics

## MCP Compatibility

DevX should expose an MCP server that maps MCP tool calls to DevX runtime capabilities.

Example MCP tools:

- `devx.search_symbol`
- `devx.search_text`
- `devx.search_references`
- `devx.read_file`
- `devx.read_type`
- `devx.read_method`
- `devx.patch_create`
- `devx.patch_validate`
- `devx.patch_apply`
- `devx.patch_rollback`
- `devx.build`
- `devx.test`
- `devx.report`
- `devx.health`
- `devx.capabilities`

MCP is an interoperability interface.

The native DevX CLI/API remains the canonical implementation.

## Agent Usage Rules

Agents using DevX should follow this workflow:

```text
discover
↓
read
↓
create patch
↓
validate patch
↓
apply patch
↓
re-index
↓
build
↓
test
↓
report
```

Agents must not silently fall back to direct repository access.

If DevX cannot satisfy a request, the agent must report:

- attempted DevX command
- missing capability
- reason for failure
- proposed fallback

## Non-Goals

ADR-012 does not define:

- Engineering Session Harness behavior
- EverCode governance
- Reasoning Engine behavior
- Development Engine replacement
- assistant-ui UX
- model/provider selection policy
- coding-agent orchestration

DevX remains a tool/runtime layer.

## Migration Strategy

### Phase 1: Runtime Capability Manifest

Implement:

- `devx capabilities`
- stable command examples
- supported operation lists
- runtime/project descriptors

### Phase 2: Health and Doctor

Implement:

- `devx health`
- `devx doctor`
- structured JSON output
- remediation hints

### Phase 3: Patch Lifecycle Hardening

Align:

- patch create
- patch validate
- patch apply
- patch rollback

Ensure all supported patch operations work end-to-end.

### Phase 4: Runtime-Aware Validation

Add:

- web runtime build/test
- .NET runtime build/test refinements
- explicit project/runtime selection
- focused test selection

### Phase 5: Incremental Indexing

Add:

- index file
- index files
- index changed
- automatic post-patch index refresh

### Phase 6: MCP Server

Expose DevX capabilities through MCP for external AI clients and IDE hosts.

## Success Criteria

ADR-012 is successful when:

1. Agents can discover DevX capabilities without guessing command syntax.
2. Patch create, validate, apply, and rollback are consistent.
3. Web and .NET build/test validation are first-class runtime capabilities.
4. DevX can report health and diagnose workspace issues.
5. DevX can be used through both native CLI and MCP-compatible interfaces.
6. Engineering Session hosts can depend on DevX as a stable runtime substrate.
7. Direct repository access becomes a fallback rather than normal operation.

## Guiding Principle

DevX owns engineering tools.

Harnesses and agents decide what to do.

DevX makes engineering actions discoverable, executable, validated, reportable, and reversible.
