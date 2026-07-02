# TASK-030: Implement Index Status Command

**Status:** Ready  
**Phase:** v0.3 – Repository Indexing UX  
**Depends On:** TASK-029C  
**Related:** TASK-029

## Objective

Add an `ecp index status` command that tells the user or agent whether the repository index exists and whether it appears current or stale.

This is a lightweight step before full incremental indexing.

## Motivation

Agents currently run:

```bash
./ecp index .
```

too often.

That wastes time and makes every task start with a full indexing pass.

Before implementing full incremental indexing, ECP should expose repository index state so agents can decide whether indexing is necessary.

## CLI

```bash
./ecp index status
./ecp index status --format json
```

Optional:

```bash
./ecp index status <repo-path> --format json
```

## Response

Example JSON:

```json
{
  "RepositoryIndexed": true,
  "RepositoryRoot": "/path/to/repo",
  "IndexedFiles": 22,
  "IndexExists": true,
  "Status": "Current",
  "Reason": "No obvious stale-index signal detected."
}
```

If no index exists:

```json
{
  "RepositoryIndexed": false,
  "RepositoryRoot": "/path/to/repo",
  "IndexedFiles": 0,
  "IndexExists": false,
  "Status": "Missing",
  "Reason": "Repository has not been indexed yet."
}
```

If stale detection is basic for now:

```json
{
  "RepositoryIndexed": true,
  "Status": "Unknown",
  "Reason": "Incremental fingerprinting is not implemented yet."
}
```

## Requirements

- Add `ecp index status`.
- Return deterministic JSON with `--format json`.
- Do not mutate the repository.
- Do not re-index.
- Do not parse source files.
- Report:
  - whether index DB exists
  - repository root
  - indexed file count
  - repository id if available
  - current/stale/missing/unknown status
  - reason
- Record operation metrics if a session is supplied.

Optional:

```bash
./ecp index status --session <id> --format json
```

## Implementation Notes

This task should not implement full incremental indexing.

It should only expose enough state for agents to avoid unnecessary indexing.

Full incremental indexing remains a later task.

## Tests

Add tests for:

- status when no index exists
- status after indexing a repository
- JSON shape is deterministic
- command does not mutate indexed data

## Validation

```bash
./ecp index status --format json
./ecp index .
./ecp index status --format json

SESSION_ID=$(./ecp session create --format json | jq -r '.Data.SessionId')
./ecp index status --session "$SESSION_ID" --format json
./ecp report --session "$SESSION_ID" --format json

./ecp build --session "$SESSION_ID" --format json
./ecp test --session "$SESSION_ID" --format json
```

If `jq` is unavailable, copy the session id manually.

## Acceptance Criteria

- `ecp index status` works.
- It does not re-index the repository.
- It returns deterministic JSON.
- Agents can use it before deciding whether to run `ecp index .`.
- Build and tests pass.
- Report records the operation when session is provided.

## Definition of Done

An agent can ask ECP whether the repository is indexed before running `ecp index .`, reducing unnecessary indexing during dogfooding.
