# Implementation Plan: DevX Engineering Runtime and MCP-Compatible Tool Server

**Branch**: `010-devx-runtime-mcp` | **Date**: 2026-07-08 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `docs/tasks/spec.md`

## Summary

Harden DevX into a stable engineering runtime contract that hosts and external clients can discover, validate, and trust without guessing. The implementation adds runtime-owned capability discovery and negotiation, runtime context selection, runtime health and diagnostics, patch lifecycle parity, explicit runtime/project validation targeting, incremental indexing, structured/versioned reporting, controlled fallback accounting, and a minimal MCP-compatible reference server while preserving current CLI behavior and existing agent workflow compatibility.

Implementation will proceed in seven validation batches: Capability Manifest + Runtime Health Foundation, Patch Lifecycle Hardening, Runtime-Aware Validation, Incremental Indexing, Fallback Reporting, MCP Reference Server, and Final Compatibility Validation.

ADR-012 implementation belongs in this DevX repository. EverCode remains a runtime consumer and should not host runtime implementation work for this ADR.

## Technical Context

**Language/Version**: C# on .NET 10 for the DevX runtime and CLI, with Markdown planning artifacts under `docs/tasks/`

**Primary Dependencies**: DevX runtime modules, capability registry/resolver infrastructure, DevX CLI contract surfaces, DevX MCP reference surface, current `devx` CLI/session/report commands, xUnit tests in `tests/DevX.Tests`, and the feature documents under `docs/tasks/`

**Storage**: DevX runtime state and report storage in this repository plus existing `.devx/devx.db` runtime state used for validation from this workspace; no new data migration is allowed

**Testing**: DevX contract and integration tests in `tests/DevX.Tests` plus direct `devx build/test/report` validation for end-to-end runtime behavior

**Target Platform**: DevX runtime repository and local engineering workspaces containing application-oriented and web-oriented projects, with EverCode and future hosts integrating through CLI or MCP-compatible runtime contracts

**Project Type**: Capability-first engineering runtime with runtime-owned capability registry plus CLI and MCP projections

**Performance Goals**: Capability discovery and health reporting should be fast enough for routine agent startup and operator checks; supported build/test/patch/report flows should complete through DevX in normal operation without making fallback the default path; report output must be detailed enough for audit without requiring manual reconstruction

**Constraints**: Preserve existing successful agent workflows; preserve current `devx` CLI semantics unless additive or explicitly version-gated; keep dual-mode output with versioned JSON as the formal compatibility promise; do not introduce data migration; do not turn DevX into a reasoning engine, host, or orchestration layer; MCP scope is a minimal working reference implementation, not a production-hardened service

**Scale/Scope**: One engineering runtime contract used by EverCode, Engineering Session, local/IDE hosts, and external AI clients; support first-class application-oriented and web-oriented validation targets; cover supported patch operations, fallback accounting, and reference-level MCP interoperability in the DevX repository while keeping EverCode as a consumer

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

- The checked-in constitution file is still a placeholder template, so no additional ratified project constitution gates are enforceable from that document.
- Planning therefore applies the active repository guidance from `AGENTS.md`, the approved intent, and ADR-012:
  - DevX must become both a stable platform contract and the default path for supported engineering operations.
  - Every engineering action must exist first as a runtime capability; CLI commands and MCP tools project those capabilities.
  - Runtime owns capability registration, resolution, selection, and execution context.
  - Existing CLI compatibility and existing successful agent workflows are both protected constraints.
  - JSON contracts are the compatibility promise and must remain versioned and backward-compatible.
  - Controlled fallback is allowed only as an explicit, logged, measurable escape hatch.
  - No EverCode UX expansion, host governance change, or data migration is allowed in this feature.
  - MCP scope is limited to a documented and testable reference surface.
  - Validation must include DevX runtime tests, focused EverCode API/web compatibility checks, and direct `devx build/test/report`.
- Gate result before research: pass.
- Gate result after design: pass, provided implementation keeps DevX as a tool/runtime layer, treats fallback accounting as a first-class contract concern, and avoids slipping into host orchestration or production MCP hosting scope.

## Project Structure

## Architecture

```text
AI Agent / EverCode / Future Host
                │
                ▼
   DevX CLI / DevX MCP / Future API
                │
                ▼
     Runtime Capability Registry
                │
                ▼
     Runtime Resolver + Context
                │
                ▼
         Execution Layer
                │
                ▼
           Repository
```

**Architecture Principle**: Every engineering action must exist first as a runtime capability. CLI commands, MCP tools, and future APIs are interface projections over a runtime-owned capability layer rather than peer abstractions.

### Documentation (this feature)

```text
docs/tasks/
├── spec.md
├── plan.md
└── tasks.md
```

### Implementation Repository

```text
src/
├── DevX.Application/
├── DevX.Cli/
├── DevX.Domain/
├── DevX.Indexer/
├── DevX.Infrastructure/
└── DevX.Protocol/

tests/
└── DevX.Tests/
```

**Structure Decision**: Keep ADR-012 implementation in the DevX repository with a runtime-owned capability-first layout. Runtime manages capability registry, resolver, selection, negotiation, and execution context. CLI and MCP remain projections of that shared runtime contract. End-to-end runtime validation is still performed through the existing `devx` CLI against this workspace.

**Delivery Decision**: Execute the work in seven batches that match the clarified intent: Capability Manifest + Runtime Health Foundation, Patch Lifecycle Hardening, Runtime-Aware Validation, Incremental Indexing, Fallback Reporting, MCP Reference Server, and Final Compatibility Validation.

## Current Status

The first runtime slice is already implemented in this repository:

- Capability discovery exposes runtime identity, default context, capability identifiers, prerequisites, and availability metadata.
- `devx health` and `devx doctor` project a shared runtime health evaluation with `Healthy`, `Degraded`, and `Unavailable` semantics.
- New runtime metadata remains behind explicit JSON mode so human-default CLI behavior stays compatible.

The second runtime slice is now in progress and partially implemented:

- Patch `create/preview/validate/apply/rollback` share one lifecycle contract with explicit rollback availability metadata.
- Build and test now project a first-class runtime context and selected target in machine-readable responses.
- Explicit runtime/project targeting is available through protocol payloads and CLI `--runtime` plus `--target` options while default selection remains backward-compatible.

The MCP reference slice is now implemented as a minimal reference surface:

- A dedicated `McpReferenceServer` projects runtime capabilities into MCP tool descriptors instead of introducing a second execution stack.
- The reference surface currently exposes discovery plus `search`, `read`, `patch.validate`, `patch.apply`, and `report`.
- File reads default to full-file projection unless the MCP caller explicitly requests a range, keeping the MCP surface host-friendly without changing protocol validation rules.
- Scope stays intentionally narrow: no production hosting, no auth, no remote multi-user concerns, and no EverCode UI affordances.

## Runtime-Aware Validation Decisions

- Runtime target discovery is owned by a shared runtime resolver in `DevX.Application` rather than hidden inside CLI command parsing.
- Validation responses now return `CapabilityId`, `RuntimeContext`, `Target`, and `Execution` so hosts can understand exactly what DevX ran.
- Explicit selection uses `runtime` and `target` in protocol payloads and `--runtime` / `--target` in CLI commands.
- Default behavior remains compatibility-safe: if no explicit target is provided, DevX still chooses the best available target instead of failing closed.
- Ask/session orchestration remains host-owned; the new targeting logic lives in runtime and CLI command boundaries only.

## Fallback Reporting Decisions

- Fallback remains an explicit host/operator action rather than an automatic hidden retry inside the runtime.
- Fallback events are recorded as structured operation metrics so reporting can aggregate them without introducing a second storage path.
- Each fallback record includes capability id, failed DevX command, reason, and chosen fallback action.
- Reports summarize both raw fallback events and fallback frequency by capability so repeated gaps become measurable backlog items.

## MCP Reference Server Decisions

- MCP is a projection of runtime capabilities, not a new capability host or orchestration layer.
- The first MCP surface is reference-grade only and intentionally limited to discovery plus one proven path each for search/read/patch/validation/report flows.
- Capability negotiation remains the runtime source of truth; MCP tool descriptors mirror capability identifiers instead of inventing parallel names.
- Current CLI and EverCode behavior remain unchanged because MCP support is additive and machine-consumable only.

## Validation Evidence

The following validation has already passed during ADR-012 foundation work:

- `dotnet run --project src/DevX.Cli -- capabilities --format json`
- `dotnet run --project src/DevX.Cli -- health --session 640f513d-ff42-4aca-b1bd-4fdea63c76a5 --format json`
- `dotnet run --project src/DevX.Cli -- doctor --session 640f513d-ff42-4aca-b1bd-4fdea63c76a5 --format json`
- `dotnet run --project src/DevX.Cli -- build --session 640f513d-ff42-4aca-b1bd-4fdea63c76a5 --format json`
- `dotnet run --project src/DevX.Cli -- test --session 640f513d-ff42-4aca-b1bd-4fdea63c76a5 --format json`
- `dotnet run --project src/DevX.Cli -- report --session 640f513d-ff42-4aca-b1bd-4fdea63c76a5 --format json`
- `dotnet test tests/DevX.Tests/DevX.Tests.csproj`

Observed results:

- Build succeeded with `0` warnings and `0` errors.
- Focused runtime and compatibility coverage passed with `49` tests and `0` failures.
- Focused patch lifecycle coverage passed with `8` tests and `0` failures.
- Focused fallback/reporting coverage passed with `7` tests and `0` failures.
- Focused MCP discovery and invocation coverage passed with `53` tests and `0` failures for `McpReferenceServerIntegrationTests`, `CapabilitiesComposerTests`, `ReportComposerTests`, and `ProtocolEngineTests`.
- Focused CLI/protocol compatibility closure coverage passed with `51` tests and `0` failures for `CapabilitiesComposerTests`, `ReportComposerTests`, and `ProtocolEngineTests`.
- Full runtime suite passed with `87` tests and `0` failures.
- `devx build --format json` and `devx test --format json` now return selected target metadata for the current workspace.
- `devx fallback record --format json` and `devx report --format json` now surface structured fallback event data and capability frequency.
- Health and doctor reported a healthy runtime for the current workspace.
- MCP integration tests now prove discovery plus `search`, `read`, `patch.validate`, `patch.apply`, and `report` paths without expanding production host scope.
- A fresh closure session (`3095da9f-3fd6-4fcc-ab74-bbe15687d6ee`) confirmed that `capabilities`, `health`, and `doctor` still return healthy machine-readable runtime metadata for the EverCode.Local workspace.
- The same closure session confirmed `devx test --format json` passed with `94` tests and `0` failures.
- The same closure session hit a transient `MSB4166` child-node shutdown during `devx build --format json`; this is an execution-environment failure, not a runtime contract-shape failure.
- The same closure session returned structured `devx report --format json` output, but `BuildTestMetrics` did not yet reflect the preceding build/test invocations. Treat that as post-closure backlog for runtime reporting fidelity, not as a blocker for reference-grade MCP scope.

Validation note:

- One failed `test` entry remains in the session report history from a transient parallel validation run while command descriptions were being reconciled. A subsequent serial `devx test` run passed and confirmed no product regression remained.

## Complexity Tracking

> No constitution violations require justification at this stage.

## Closure Status

ADR-012 is complete at the reference/runtime hardening level.

Closure includes:

- capability-first runtime contracts
- health and doctor validation
- patch lifecycle hardening
- runtime-aware build/test targeting
- incremental indexing
- fallback metrics
- MCP reference-surface support

Tracked follow-up backlog:

- Build-runtime reliability follow-up: transient `MSB4166` child-node shutdown during closure `build` validation
- Report/session correlation bug: fresh-session `report` output did not yet reflect prior build/test activity in `BuildTestMetrics`

Production MCP hardening is intentionally not started in this ADR scope.
