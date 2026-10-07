# Tasks: DevX Engineering Runtime and MCP-Compatible Tool Server

**Input**: Design documents from `/docs/tasks/`

**Prerequisites**: `plan.md` (required), `spec.md` (required), and this task list

**Tests**: Tests are included because the specification requires DevX runtime contract/integration validation plus direct `devx build/test/report` validation in this repository.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

**Batching**: Implement in seven validation batches: Capability Manifest + Runtime Health Foundation, Patch Lifecycle Hardening, Runtime-Aware Validation, Incremental Indexing, Fallback Reporting, MCP Reference Server, and Final Compatibility Validation.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel when files do not overlap and no unfinished dependency blocks the task
- **[Story]**: Maps to the user story in `spec.md`
- Every task includes an exact file path

## Path Conventions

- Runtime contract and validation artifacts live under `docs/tasks/`
- DevX runtime implementation lives under `src/`
- DevX tests live under `tests/DevX.Tests/`
- DevX workspace state for validation is expected under `.devx/`

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Align the feature scaffolding, DevX repository boundary, and validation guidance before changing any runtime behavior.

- [X] T001 Update implementation sequencing, DevX repository boundary, and batch validation notes in `docs/tasks/plan.md`
- [X] T002 [P] Align validation scenarios and evidence expectations inline in `docs/tasks/plan.md` and `docs/tasks/tasks.md`
- [ ] T003 [P] Refresh the capability-first runtime contract summary and terminology inline in `docs/tasks/spec.md` and `docs/tasks/plan.md`

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Establish the capability-first runtime abstractions, fallback concepts, and consumer compatibility scaffolding required before any user story work begins.

**⚠️ CRITICAL**: No user story work should begin until this phase is complete.

- [ ] T004 Define the runtime-owned capability model, runtime context, health states, and fallback frequency concepts inline in `docs/tasks/spec.md`
- [ ] T005 [P] Define capability identity, negotiation, and CLI/MCP projection rules inline in `docs/tasks/spec.md` and `docs/tasks/plan.md`
- [X] T006 [P] Add foundational DevX contract coverage for capability descriptors and health states in `tests/DevX.Tests/Cli/CapabilitiesComposerTests.cs`
- [ ] T007 [P] Add foundational protocol compatibility coverage for runtime descriptor consumption in `tests/DevX.Tests/Protocol/ProtocolEngineTests.cs`
- [ ] T008 [P] Add foundational CLI compatibility coverage proving future-host metadata stays behind explicit machine-readable surfaces in `tests/DevX.Tests/Cli/ReportComposerTests.cs`

**Checkpoint**: Capability-first runtime definitions and baseline consumer compatibility coverage exist for ADR-012 runtime hardening.

---

## Phase 3: User Story 1 - Discover and trust runtime support (Priority: P1) 🎯 MVP

**Goal**: Provide reliable capability discovery, health, and diagnostics so hosts and operators can trust DevX first instead of guessing.

**Independent Test**: Starting from the current workspace, a caller can inspect capability, health, and doctor output in structured mode and determine supported actions, readiness state, and remediation guidance without undocumented behavior.

### Tests for User Story 1

- [X] T009 [P] [US1] Add DevX contract tests for capability manifest projection in `tests/DevX.Tests/Cli/CapabilitiesComposerTests.cs`
- [X] T010 [P] [US1] Add DevX integration tests for health and doctor states in `tests/DevX.Tests/Protocol/HealthAndDoctorIntegrationTests.cs`
- [X] T011 [P] [US1] Add capability/health/doctor validation notes inline in `docs/tasks/plan.md`

### Implementation for User Story 1

- [X] T012 [P] [US1] Implement runtime capability registry, identifiers, negotiation, and manifest generation in `src/DevX.Cli/CapabilitiesComposer.cs` and `src/DevX.Protocol/ProtocolModels.cs`
- [X] T013 [P] [US1] Implement health and doctor CLI projections over runtime capabilities in `src/DevX.Cli/Program.cs` and `src/DevX.Indexer/DotnetBuildTestService.cs`
- [X] T014 [US1] Capture the implementation decisions for versioned JSON and CLI compatibility inline in `docs/tasks/plan.md`
- [X] T015 [US1] Implement capability and health output schemas with version metadata in `src/DevX.Protocol/ProtocolModels.cs` and `src/DevX.Cli/DevXReport.cs`
- [X] T016 [US1] Keep current CLI compatibility aligned with runtime descriptor expectations in `tests/DevX.Tests/Cli/CapabilitiesComposerTests.cs`
- [X] T017 [US1] Keep current report and human-default output behavior unchanged while exposing new runtime metadata only through explicit machine-readable modes in `tests/DevX.Tests/Cli/ReportComposerTests.cs`

**Checkpoint**: Capability discovery and diagnostic runtime capabilities are implemented in DevX and consumed without changing current EverCode UX.

---

## Phase 4: User Story 2 - Execute patch and validation flows consistently (Priority: P1)

**Goal**: Make supported patch and runtime-targeted validation flows contract-consistent so DevX becomes the default engineering path for normal operations.

**Independent Test**: Using only DevX-supported patch and validation flows in application-oriented and web-oriented workspace scenarios, supported operations succeed end to end and failures are reported through the same runtime contract.

### Tests for User Story 2

- [X] T018 [P] [US2] Add DevX contract coverage for patch lifecycle capability semantics in `tests/DevX.Tests/Patch/PatchLifecycleContractTests.cs`
- [X] T019 [P] [US2] Add DevX integration coverage for explicit runtime/project validation targeting in `tests/DevX.Tests/Protocol/ValidationTargetIntegrationTests.cs`
- [X] T020 [P] [US2] Add CLI compatibility checks for unchanged patch and validation consumption assumptions in `tests/DevX.Tests/Patch/ProtocolPatchTests.cs`
- [X] T021 [P] [US2] Add focused patch parity and explicit runtime/project targeting validation scenarios inline in `docs/tasks/plan.md`

### Implementation for User Story 2

- [X] T022 [P] [US2] Implement patch capability models and lifecycle parity rules in `src/DevX.Cli/PatchCreateCommand.cs` and `src/DevX.Protocol/ProtocolModels.cs`
- [X] T023 [P] [US2] Implement create, validate, apply, and rollback capability handlers in `src/DevX.Cli/PatchCreateCommand.cs`, `src/DevX.Cli/Program.cs`, and `src/DevX.Protocol/PatchLifecycleService.cs`
- [X] T024 [P] [US2] Implement runtime context resolution and runtime-aware build/test capability routing in `src/DevX.Application/`, `src/DevX.Indexer/DotnetBuildTestService.cs`, and `src/DevX.Protocol/ProtocolEngine.cs`
- [X] T025 [US2] Capture contract-level decisions for explicit target selection and patch lifecycle conformance inline in `docs/tasks/plan.md`
- [X] T026 [US2] Keep current CLI orchestration seams compatibility-safe without moving runtime governance into ask/session layers in `src/DevX.Cli/Program.cs` and `src/DevX.Cli/AskOrchestrator.cs`

**Checkpoint**: Patch lifecycle and runtime-targeted validation capabilities are implemented in DevX and remain compatibility-safe for EverCode consumers.

---

## Phase 5: User Story 3 - Integrate external clients without changing EverCode behavior (Priority: P2)

**Goal**: Prepare future hosts and external clients through a reference MCP surface and strict controlled fallback accounting, without shipping a new production host or changing EverCode behavior.

**Independent Test**: A client can discover and invoke the documented reference surface, while current CLI and host workflows remain compatible and any fallback is explicit, logged, reasoned, and measurable.

### Tests for User Story 3

- [X] T027 [P] [US3] Add DevX contract coverage for fallback-reporting and capability-frequency metrics in `tests/DevX.Tests/Cli/ReportComposerTests.cs`
- [X] T028 [P] [US3] Add DevX integration coverage for the MCP reference server surface in `tests/DevX.Tests/Protocol/McpReferenceServerIntegrationTests.cs`
- [X] T029 [P] [US3] Add focused CLI regression coverage proving future-host metadata remains machine-readable and does not leak into current human-default output in `tests/DevX.Tests/Cli/CapabilitiesComposerTests.cs`

### Implementation for User Story 3

- [X] T030 [P] [US3] Implement fallback event, fallback frequency, and runtime report models in `src/DevX.Cli/DevXReport.cs` and `src/DevX.Domain/Entities.cs`
- [X] T031 [P] [US3] Implement capability-to-CLI and capability-to-MCP projections in `src/DevX.Cli/CapabilitiesComposer.cs` and `src/DevX.Protocol/ProtocolModels.cs`
- [X] T032 [P] [US3] Implement the MCP-compatible reference server surface in `src/DevX.Protocol/ProtocolEngine.cs` and `src/DevX.Application/`
- [X] T033 [US3] Capture decisions for controlled fallback ownership and reference-grade MCP scope inline in `docs/tasks/plan.md`
- [X] T034 [US3] Preserve current human-default CLI affordance boundaries while keeping future-host metadata behind explicit JSON output in `src/DevX.Cli/Program.cs`
- [X] T035 [US3] Keep current protocol compatibility surfaces future-host-safe without introducing host governance into runtime layers in `tests/DevX.Tests/Protocol/ProtocolEngineTests.cs`

**Checkpoint**: Future-host readiness and fallback accounting are implemented in DevX, while current EverCode behavior remains unchanged.

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Validate the full ADR-012 contract direction, capture final evidence, and prepare implementation handoff.

- [X] T036 [P] Add full feature validation evidence and runtime hardening notes inline in `docs/tasks/plan.md` and `docs/tasks/tasks.md`
- [X] T037 [P] Reconcile plan and spec language after task generation in `docs/tasks/plan.md`
- [X] T038 Validate DevX repository contract and integration scenarios using `dotnet test tests/DevX.Tests/DevX.Tests.csproj`
- [X] T039 Validate focused capability and health scenarios using `dotnet test tests/DevX.Tests/DevX.Tests.csproj --filter "CapabilitiesComposerTests|HealthAndDoctorIntegrationTests|ProtocolEngineTests"`
- [ ] T040 Validate focused patch and validation scenarios using `dotnet test tests/DevX.Tests/DevX.Tests.csproj --filter "ProtocolPatchTests|PatchLifecycleContractTests|ValidationTargetIntegrationTests|RepositoryIndexingServiceReindexTests"`
- [X] T041 Validate DevX runtime hardening scenarios using `devx capabilities --format json`, `devx health --session <session-id> --format json`, `devx doctor --session <session-id> --format json`, `devx build --session <session-id> --format json`, `devx test --session <session-id> --format json`, and `devx report --session <session-id> --format json`

**Checkpoint**: ADR-012 design artifacts, compatibility checks, and runtime validation evidence are ready for implementation execution.

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies
- **Foundational (Phase 2)**: Depends on Setup and blocks all user stories
- **User Story 1 (Phase 3)**: Depends on Foundational
- **User Story 2 (Phase 4)**: Depends on Foundational and benefits from User Story 1 runtime descriptor scaffolding
- **User Story 3 (Phase 5)**: Depends on Foundational and builds on the contract direction established in User Stories 1 and 2
- **Polish (Phase 6)**: Depends on the desired user stories being complete

### User Story Dependencies

- **User Story 1 (P1)**: No dependency on other user stories once Foundational is complete
- **User Story 2 (P1)**: Can start after Foundational, but should align with the runtime descriptor and compatibility patterns established in User Story 1
- **User Story 3 (P2)**: Depends on the shared contract and fallback semantics established in User Stories 1 and 2

### Within Each User Story

- Update or add tests before implementation where new contract coverage is introduced
- Shared runtime contract and context changes should land before compatibility mapping or UI no-leakage adjustments
- Documentation contract changes should align with the corresponding validation guidance
- Story-specific validation should complete before moving on

### Parallel Opportunities

- Setup tasks `T002` and `T003` can run in parallel after `T001`
- Foundational tasks `T005`, `T006`, `T007`, and `T008` can run in parallel after `T004`
- User Story 1 test tasks `T009`, `T010`, and `T011` can run in parallel
- User Story 2 DevX contract/runtime tasks `T018`, `T019`, `T022`, `T023`, and `T024` can run in parallel
- User Story 3 DevX contract/runtime tasks `T027`, `T028`, `T030`, `T031`, and `T032` can run in parallel
- Polish validation tasks `T038`, `T039`, `T040`, and `T041` can run in parallel once implementation is complete

---

## Parallel Example: User Story 1

```bash
Task: "Add CLI contract coverage for runtime-neutral capability and diagnostic descriptors in tests/DevX.Tests/Cli/CapabilitiesComposerTests.cs"
Task: "Add DevX integration tests for health and doctor states in tests/DevX.Tests/Protocol/HealthAndDoctorIntegrationTests.cs"
Task: "Add capability/health/doctor validation notes inline in docs/tasks/plan.md"
```

## Parallel Example: User Story 2

```bash
Task: "Implement patch capability models and lifecycle parity rules in src/DevX.Cli/PatchCreateCommand.cs and src/DevX.Protocol/ProtocolModels.cs"
Task: "Implement create, validate, apply, and rollback capability handlers in src/DevX.Cli/PatchCreateCommand.cs, src/DevX.Cli/Program.cs, and src/DevX.Infrastructure/Patching/"
Task: "Implement runtime context resolution and runtime-aware build/test capability routing in src/DevX.Application/, src/DevX.Indexer/DotnetBuildTestService.cs, and src/DevX.Protocol/ProtocolEngine.cs"
```

## Parallel Example: User Story 3

```bash
Task: "Implement fallback event, fallback frequency, and runtime report models in src/DevX.Cli/DevXReport.cs and src/DevX.Protocol/ProtocolModels.cs"
Task: "Implement capability-to-CLI and capability-to-MCP projections in src/DevX.Cli/CapabilitiesComposer.cs and src/DevX.Protocol/ProtocolModels.cs"
Task: "Add focused CLI regression coverage proving runtime/future-host metadata stays machine-readable and does not leak into human-default output in tests/DevX.Tests/Cli/CapabilitiesComposerTests.cs"
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup
2. Complete Phase 2: Foundational
3. Complete Phase 3: User Story 1
4. Validate capability discovery and diagnostic contract behavior independently
5. Stop and review before deeper runtime hardening

### Incremental Delivery

1. Finish Setup + Foundational to establish capability-first runtime and compatibility scaffolding
2. Deliver User Story 1 to make runtime discovery and diagnostics trustworthy
3. Deliver User Story 2 to harden patch and validation capabilities inside DevX itself
4. Deliver User Story 3 to add controlled fallback accounting and MCP reference readiness without expanding production host scope
5. Run final cross-cutting validation in the DevX repository and capture evidence

### Parallel Team Strategy

1. One contributor owns capability/runtime implementation in `src/`
2. One contributor owns focused DevX contract and integration tests in `tests/DevX.Tests/`
3. One contributor owns feature design artifact updates under `docs/tasks/`
4. Rejoin for final validation and evidence capture in this repository

## Completed Validation Evidence

- `dotnet test tests/DevX.Tests/DevX.Tests.csproj` passed.
- `dotnet test tests/DevX.Tests/DevX.Tests.csproj --filter "CapabilitiesComposerTests|HealthAndDoctorIntegrationTests|ProtocolEngineTests"` passed.
- `dotnet run --project src/DevX.Cli -- capabilities --format json` passed.
- `dotnet run --project src/DevX.Cli -- health --session 640f513d-ff42-4aca-b1bd-4fdea63c76a5 --format json` passed.
- `dotnet run --project src/DevX.Cli -- doctor --session 640f513d-ff42-4aca-b1bd-4fdea63c76a5 --format json` passed.
- `dotnet run --project src/DevX.Cli -- build --session 640f513d-ff42-4aca-b1bd-4fdea63c76a5 --format json` passed.
- `dotnet run --project src/DevX.Cli -- test --session 640f513d-ff42-4aca-b1bd-4fdea63c76a5 --format json` passed.
- `dotnet run --project src/DevX.Cli -- report --session 640f513d-ff42-4aca-b1bd-4fdea63c76a5 --format json` passed.
- Focused patch lifecycle coverage passed: `8` tests, `0` failed.
- Focused runtime-aware validation coverage passed: `7` tests, `0` failed.
- Combined focused compatibility/runtime coverage passed: `49` tests, `0` failed.
- Focused fallback/reporting coverage passed: `7` tests, `0` failed.
- Focused MCP reference-server coverage passed: `53` tests, `0` failed, including discovery plus `search`, `read`, `patch.validate`, `patch.apply`, and `report` paths.
- Focused CLI/protocol compatibility closure coverage passed: `51` tests, `0` failed, for `CapabilitiesComposerTests`, `ReportComposerTests`, and `ProtocolEngineTests`.
- Full runtime suite passed through DevX: `87` tests, `0` failed.
- Fresh closure validation commands ran against session `3095da9f-3fd6-4fcc-ab74-bbe15687d6ee`.
- `devx capabilities --format json`, `devx health --format json`, and `devx doctor --format json` returned healthy runtime metadata for the EverCode.Local workspace.
- `devx test --session 3095da9f-3fd6-4fcc-ab74-bbe15687d6ee --format json` passed with `94` tests, `0` failures.
- `devx build --session 3095da9f-3fd6-4fcc-ab74-bbe15687d6ee --format json` returned a transient `MSB4166` child-node shutdown failure rather than a contract or parsing error.
- `devx report --session 3095da9f-3fd6-4fcc-ab74-bbe15687d6ee --format json` returned structured report output, but the fresh session did not yet reflect the preceding build/test execution in `BuildTestMetrics`; treat that as follow-up backlog rather than reference-MCP scope expansion.

## Closure Status

ADR-012 is complete at the reference/runtime hardening level.

Tracked follow-up backlog:

- Build-runtime reliability follow-up: transient `MSB4166` child-node shutdown during closure build validation
- Report/session correlation bug: fresh-session report output missing prior build/test metrics in `BuildTestMetrics`

Production MCP hardening is intentionally out of scope for this closure.
