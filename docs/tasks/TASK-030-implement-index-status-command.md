# TASK-030: Implement Index Status Command

**Status:** Ready  
**Phase:** v0.3 – Repository Indexing UX  
**Depends On:** TASK-029C  
**Related:** TASK-029

## Objective

Add an `devx index status` command that tells the user or agent whether the repository index exists and whether it appears current or stale.

This is a lightweight step before full incremental indexing.

## Motivation

Agents currently run:

```bash
./devx index .
```

too often.

That wastes time and makes every task start with a full indexing pass.

Before implementing full incremental indexing, DevX should expose repository index state so agents can decide whether indexing is necessary.

## CLI

```bash
./devx index status
./devx index status --format json
```

Optional:

```bash
./devx index status <repo-path> --format json
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

- Add `devx index status`.
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
./devx index status --session <id> --format json
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
./devx index status --format json
./devx index .
./devx index status --format json

SESSION_ID=$(./devx session create --format json | jq -r '.Data.SessionId')
./devx index status --session "$SESSION_ID" --format json
./devx report --session "$SESSION_ID" --format json

./devx build --session "$SESSION_ID" --format json
./devx test --session "$SESSION_ID" --format json
```

If `jq` is unavailable, copy the session id manually.

## Acceptance Criteria

- `devx index status` works.
- It does not re-index the repository.
- It returns deterministic JSON.
- Agents can use it before deciding whether to run `devx index .`.
- Build and tests pass.
- Report records the operation when session is provided.

## Definition of Done

An agent can ask DevX whether the repository is indexed before running `devx index .`, reducing unnecessary indexing during dogfooding.
