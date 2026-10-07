# Feature Specification: DevX Engineering Runtime and MCP-Compatible Tool Server

**Feature Branch**: `[010-devx-runtime-mcp]`

**Created**: 2026-07-08

**Status**: Draft

**Input**: User description: "docs/intents/INTENT-0011-Implement-ADR-012-DevX-Engineering-Runtime-and-MCP-Compatible-Tool-Server.md"

## Implementation Boundary

This specification defines ADR-012 work for the DevX repository and runtime codebase.

- Runtime implementation belongs in DevX itself.
- EverCode remains a consumer of that runtime.
- Work in the EverCode repository is limited to consumer compatibility, integration validation, and no-leakage checks.

Every engineering action must exist first as a runtime capability. CLI commands, MCP tools, and future APIs are projections of those capabilities rather than the capabilities themselves.

Runtime owns capability registration, resolution, selection, and execution context. Capability catalogs must not become a second parallel runtime abstraction.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Discover and trust runtime support (Priority: P1)

As an AI-assisted engineering host or agent operator, I can inspect what the engineering runtime supports, whether it is healthy in the current workspace, and how to recover from problems so I can rely on DevX first instead of guessing or bypassing it.

**Why this priority**: Without reliable capability discovery and health guidance, every other runtime operation remains uncertain and hosts continue to use direct repository or shell fallbacks by default.

**Independent Test**: Can be fully tested by starting in a workspace, inspecting capability and health output, and confirming an operator or agent can determine supported actions, current readiness, and remediation steps without using undocumented behavior.

**Acceptance Scenarios**:

1. **Given** a supported workspace, **When** a caller requests capability discovery, **Then** the runtime returns a stable description of supported operations, runtimes, and output contracts.
2. **Given** a workspace with missing prerequisites or degraded runtime state, **When** a caller requests health or diagnostics, **Then** the runtime reports readiness status, specific failures, and actionable remediation guidance.

---

### User Story 2 - Execute patch and validation flows consistently (Priority: P1)

As an agent or host using DevX to make engineering changes, I can create, validate, apply, and reverse supported patch operations and run runtime-targeted validation through the same runtime contract so I do not need to switch to direct edits or ad hoc command execution in normal flows.

**Why this priority**: Patch and validation consistency is the core trust boundary that determines whether DevX can become the default engineering runtime instead of a partial helper.

**Independent Test**: Can be fully tested by using only DevX-supported patch and validation flows in both application and web workspaces, verifying that supported operations succeed end to end and that failures are reported through the same contract.

**Acceptance Scenarios**:

1. **Given** a supported change request, **When** a caller performs patch creation, validation, apply, and rollback for a supported operation, **Then** each stage understands the same operation class and reports consistent outcomes.
2. **Given** a workspace containing more than one runtime or project target, **When** a caller requests build or test execution for a selected target, **Then** the runtime executes the requested validation against the chosen target without requiring the caller to guess project or runtime context.

---

### User Story 3 - Integrate external clients without changing EverCode behavior (Priority: P2)

As a future host or external AI client, I can access DevX through a documented CLI and MCP-compatible reference surface, while current EverCode and existing agent workflows remain compatible during rollout.

**Why this priority**: Future-host readiness matters, but only after the runtime is trustworthy and backward-compatible for current users.

**Independent Test**: Can be fully tested by confirming that a client can use the documented reference surface to discover and invoke runtime operations, while existing CLI and host workflows continue to work and any fallback remains explicit and measurable.

**Acceptance Scenarios**:

1. **Given** an external client that supports MCP-style tool invocation, **When** it connects to the DevX reference surface, **Then** it can discover and invoke the documented runtime operations through the same capability model used by the CLI contract.
2. **Given** a supported operation that still fails at runtime during rollout, **When** a caller uses an approved escape hatch, **Then** the fallback is explicit, logged, reasoned, countable, and visible in runtime reporting.

---

### Edge Cases

- What happens when a workspace contains both application and web targets and the caller omits target selection?
- How does the runtime behave when it advertises support for an operation but a required local dependency is missing at execution time?
- What happens when only part of a multi-file patch can be reversed?
- How does capability discovery behave when indexing is stale, incomplete, or currently unavailable?
- How are repeated fallback events surfaced when different hosts hit the same runtime failure for the same advertised capability?

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The system MUST expose a discoverable runtime capability manifest that describes supported capability identifiers, operation categories, available runtime targets, supported output formats, and usage examples.
- **FR-001A**: The runtime capability model MUST be the primary abstraction, and CLI commands plus MCP tools MUST project that capability model rather than define separate runtime semantics.
- **FR-002**: The system MUST provide readiness reporting that states whether the runtime can be used successfully in the current workspace and identifies blocking conditions when it cannot.
- **FR-002A**: Runtime health MUST classify status using at least Healthy, Degraded, and Unavailable states.
- **FR-003**: The system MUST provide diagnostic guidance that explains how to remediate common runtime readiness failures.
- **FR-004**: The system MUST support stable, versioned machine-readable output for capability discovery, readiness, diagnostics, patch lifecycle, validation, reporting, and fallback accounting.
- **FR-005**: The system MUST preserve a human-usable command-line experience while also supporting an explicit stable machine-readable mode for hosts and agents.
- **FR-006**: The system MUST support the same advertised patch operation classes across patch creation, validation, application, and reversal wherever reversal is possible.
- **FR-007**: The system MUST make rollback availability explicit for each supported patch result, including cases where rollback is partial or unavailable.
- **FR-008**: The system MUST support runtime-aware validation for both application-oriented and web-oriented project targets through explicit target selection.
- **FR-009**: The system MUST allow callers to target a specific runtime and project context without relying on implicit workspace guesses when multiple valid targets exist.
- **FR-010**: The system MUST support incremental indexing for newly created, changed, and explicitly listed files.
- **FR-011**: The system MUST refresh or clearly invalidate runtime discovery results when indexing state changes materially.
- **FR-012**: The system MUST produce structured reports that summarize discovery, readiness, patch, validation, and fallback activity for audit and debugging purposes.
- **FR-013**: The system MUST provide a documented and testable MCP-compatible reference surface that exposes the same runtime capability model as the primary runtime contract.
- **FR-014**: The system MUST preserve compatibility for existing successful agent workflows during rollout, including cases where a controlled fallback is temporarily required.
- **FR-015**: The system MUST preserve compatibility for current CLI users unless a behavior change is additive or explicitly version-gated.
- **FR-016**: The system MUST require DevX-first execution for supported operations before any fallback path is used.
- **FR-017**: The system MUST make any fallback explicit and include the failed runtime command, failure reason, and resulting fallback action in runtime reporting.
- **FR-018**: The system MUST expose measurable fallback counts in machine-readable reporting so repeated fallback can be tracked over time.
- **FR-018A**: The system MUST expose fallback frequency per capability so the highest-friction runtime gaps can be identified and prioritized.
- **FR-019**: The system MUST support shared fallback triage by making repeated fallback cases classifiable as runtime defects/capability gaps or host integration defects.
- **FR-020**: The system MUST remain a tool/runtime layer and MUST NOT assume ownership of host governance, reasoning behavior, conversation behavior, or agent planning.
- **FR-021**: The runtime MUST expose its supported capability identifiers, version information, prerequisites, and availability through a discoverable capability negotiation mechanism.
- **FR-022**: The runtime MUST define and use a first-class Runtime Context model containing workspace, repository, project, runtime, and configuration selection data for capability execution and validation.

### Key Entities *(include if feature involves data)*

- **Runtime Capability Manifest**: The published description of supported operations, canonical capability identifiers, runtimes, selection rules, output modes, availability/prerequisite metadata, and compatibility/version information.
- **Runtime Capability**: The canonical engineering action abstraction from which CLI commands, MCP tools, and future interfaces are projected.
- **Runtime Context**: The caller-selected workspace, repository, project, runtime, and configuration context against which a capability is resolved and executed.
- **Runtime Health Record**: A snapshot of current workspace readiness, including blocking conditions, degraded states, and remediation guidance.
- **Patch Operation Result**: The record of a supported change request as it moves through create, validate, apply, and rollback stages, including reversal availability and diagnostics.
- **Validation Target**: A validation-focused view over the Runtime Context used for build or test execution.
- **Fallback Event**: A controlled exception record capturing the failed runtime command, failure reason, chosen fallback, and occurrence count.
- **MCP Reference Surface**: The documented external tool surface that mirrors runtime capabilities for interoperable clients without redefining runtime semantics.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In acceptance testing, 100% of supported runtime operations can be discovered through the published capability contract without relying on undocumented syntax or manual tribal knowledge.
- **SC-002**: In representative application and web workspace validation flows, at least 90% of supported build and test requests complete through DevX without requiring fallback in normal operation.
- **SC-003**: For every supported patch operation class, create, validate, and apply outcomes are contract-consistent in 100% of conformance test cases, and rollback availability is explicitly reported for 100% of cases.
- **SC-004**: In runtime failure scenarios exercised during validation, 100% of fallback events are reported with failed command, reason, fallback action, and count information.
- **SC-005**: External integration tests can complete capability discovery and at least one read, patch, validation, and report workflow through the MCP-compatible reference surface without requiring a separate runtime model.
- **SC-006**: Existing compatibility regression suites for current CLI and agent workflows pass without introducing mandatory migration steps for current users.

## Assumptions

- DevX remains the first and only engineering runtime being hardened in this feature; no additional production runtime implementation is required.
- The DevX repository can accept a runtime-owned source layout where capability registry, resolver, selection, and execution context live under a shared runtime boundary without forcing EverCode to host runtime implementation code.
- Current EverCode, Engineering Session, and other host UX layers will continue consuming runtime behavior through existing boundaries rather than introducing new end-user workflow surfaces in this feature.
- Runtime support for application-oriented and web-oriented project targets is sufficient for the initial release scope; future runtime families can be added later without redefining the contract.
- Controlled fallback is acceptable during rollout only when it is explicitly reported and measurable, and it is not treated as a steady-state success condition.
- The MCP-compatible server delivered in this feature is a reference implementation suitable for validation and host adoption planning, not a production-hardened multi-tenant service.
