# TASK-010: Implement CLI

- **Status:** Ready
- **Plan:** PLAN-003 Context Protocol Engine
- **Depends On:** TASK-009

## Objective

Implement the MVP command-line interface for ECP.

## Prerequisites

- Protocol dispatcher exists
- Indexing pipeline exists
- Search API exists
- Session store exists

## Inputs

- `docs/specs/SPEC-005-cli-specification.md`
- `docs/specs/SPEC-001-context-protocol.md`

## Commands

```text
ecp index
ecp search
ecp snapshot
ecp expand
ecp patch
ecp build
ecp test
ecp diagnostics
ecp ask
```

## MVP Priority

Implement in this order:

1. `ecp index`
2. `ecp search`
3. `ecp snapshot`
4. `ecp expand`
5. `ecp diagnostics`

`ecp ask` may initially be a placeholder until LLM adapters exist.

## Output Modes

The CLI should support:

```text
--format text
--format json
```

JSON output is required for automation.

## Implementation Steps

1. Add CLI parsing package if needed.
2. Define root command.
3. Implement `index`.
4. Implement `search`.
5. Implement `snapshot`.
6. Wire commands to protocol dispatcher.
7. Add command tests or integration smoke tests.

## Acceptance Criteria

- CLI runs successfully.
- `ecp index <path>` indexes a repository.
- `ecp search <query>` returns indexed symbols.
- JSON output mode works.
- Invalid commands return useful errors.

## Verification Steps

```bash
dotnet run --project src/Ecp.Cli -- --help
dotnet run --project src/Ecp.Cli -- index .
dotnet run --project src/Ecp.Cli -- search Authentication --format json
```

## Out of Scope

- Interactive chat UX
- Provider-specific LLM adapters
- Advanced formatting

## Definition of Done

The MVP CLI can index and query repositories through the ECP protocol engine.
