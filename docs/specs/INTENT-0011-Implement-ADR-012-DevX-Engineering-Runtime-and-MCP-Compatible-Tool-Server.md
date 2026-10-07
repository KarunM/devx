# INTENT-0011: Implement ADR-012 – DevX Engineering Runtime and MCP-Compatible Tool Server

**Status:** Proposed  
**Related ADR:** ADR-012 – DevX Engineering Runtime and MCP-Compatible Tool Server

## Objective

Formalize DevX as the engineering runtime used by AI assistants, Engineering Session hosts, IDE extensions, CLI hosts, and future agentic engineering tools.

DevX should provide a reliable, discoverable, validated, and reversible tool runtime for repository understanding and engineering execution.

## Problem Statement

DevX already supports many critical engineering operations, including indexing, search, reading, patching, build, test, sessions, and reports. However, usage is inconsistent because some commands are incomplete, command capabilities are not fully discoverable, patch create/validate/apply behavior is not aligned, and web/runtime validation is not first-class.

As a result, agents sometimes fall back to direct repository access, direct edits, or direct validation commands.

## Desired Outcome

DevX becomes the stable Engineering Runtime underneath:

- EverCode
- Engineering Session Harness
- local/IDE hosts
- external AI clients
- future MCP-compatible tools

DevX remains a tool/runtime layer, not an agent harness or reasoning engine.

## In Scope

- Define and implement runtime capability discovery.
- Add `devx capabilities`.
- Add `devx health`.
- Add `devx doctor`.
- Harden patch lifecycle:
  - create
  - validate
  - apply
  - rollback
- Align patch operation support across create/validate/apply.
- Add runtime-aware build/test:
  - .NET
  - web/Vite/Vitest
- Add explicit runtime/project selection.
- Add incremental indexing:
  - `devx index file`
  - `devx index files`
  - `devx index changed`
- Add structured runtime reports.
- Define MCP-compatible DevX server surface.
- Ensure agents can discover supported operations without guessing.

## Out of Scope

- Engineering Session Harness behavior.
- EverCode governance.
- Reasoning Engine changes.
- Development Engine replacement.
- AI model/provider selection.
- Conversation UI.
- Agent planning or orchestration.

## Functional Requirements

1. DevX MUST expose a capability manifest through `devx capabilities`.
2. DevX MUST expose environment readiness through `devx health`.
3. DevX MUST expose diagnostic guidance through `devx doctor`.
4. Patch operations accepted by `patch create` MUST also be supported by `patch validate`, `patch apply`, and rollback where possible.
5. DevX MUST support explicit runtime/project selection for build and test.
6. DevX MUST support web/Vite/Vitest validation as a first-class runtime.
7. DevX MUST support .NET build/test validation as a first-class runtime.
8. DevX MUST support incremental indexing for changed or newly created files.
9. DevX MUST produce structured JSON output for capabilities, health, doctor, build, test, patch, and report commands.
10. DevX MUST define an MCP-compatible tool surface for external AI clients.

## Acceptance Criteria

- Agents can inspect DevX capabilities without guessing command syntax.
- `devx health` reports whether DevX can be used successfully in the current workspace.
- `devx doctor` provides actionable diagnostic output.
- Patch create/validate/apply/rollback are consistent for supported patch operations.
- Web build/test can run through DevX without direct npm fallback.
- .NET build/test can run through DevX with explicit project/runtime targeting.
- Incremental index commands work for new, changed, and multiple files.
- DevX reports include enough detail for audit and debugging.
- MCP-compatible tool definitions are documented.
- Direct repository fallback becomes exceptional rather than normal.

## Success Measures

- Agents no longer need direct repository search/read for supported DevX operations.
- Agents no longer need direct patch/edit fallback for supported patch operations.
- Web validation no longer requires direct npm fallback.
- DevX reports clearly show discovery, patch, validation, and runtime health.
- Engineering Session hosts can rely on DevX as a stable runtime substrate.

## Implementation Notes

Implement in batches:

1. Capability manifest.
2. Health and doctor.
3. Patch lifecycle hardening.
4. Runtime-aware build/test.
5. Incremental indexing.
6. MCP-compatible server surface.
7. Final validation and documentation.

Use DevX for repository discovery, reading, build, test, and report wherever currently possible.

Where DevX lacks the capability being implemented, use a documented fallback and record the gap as part of the implementation report.
