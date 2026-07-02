# TASK-028: Implement PATCH_ROLLBACK

**Status:** Ready\
**Phase:** v0.2 -- Safe Editing\
**Depends On:** TASK-027

## Objective

Implement the `PATCH_ROLLBACK` protocol operation to safely revert a
previously applied patch.

## CLI

``` bash
./ecp patch rollback <patch-file> --session <id> --format json
```

## Requirements

-   Reverse all operations from an applied patch.
-   Refuse rollback if the patch was never applied.
-   Detect conflicts that prevent rollback.
-   Restore repository state safely.
-   Record a `patch rollback` operation in the session metrics.
-   Do not leave partial rollbacks.

## Response

``` json
{
  "Success": true,
  "FilesRestored": 2,
  "OperationsReverted": 4,
  "Warnings": [],
  "Errors": []
}
```

## Implementation

1.  Add rollback protocol models.
2.  Implement rollback engine.
3.  Wire `ProtocolEngine`.
4.  Add CLI command.
5.  Record rollback metrics.
6.  Add unit/integration tests.
7.  Update report support.

## Validation

``` bash
./ecp index .
SESSION_ID=$(./ecp session create --format json | jq -r '.Data.SessionId')

./ecp patch preview tests/patches/create-file.patch.json --session "$SESSION_ID" --format json
./ecp patch validate tests/patches/create-file.patch.json --session "$SESSION_ID" --format json
./ecp patch apply tests/patches/create-file.patch.json --session "$SESSION_ID" --format json
./ecp patch rollback tests/patches/create-file.patch.json --session "$SESSION_ID" --format json

./ecp build --session "$SESSION_ID" --format json
./ecp test --session "$SESSION_ID" --format json
./ecp report --session "$SESSION_ID" --format json
```

## Acceptance Criteria

-   `PATCH_ROLLBACK` implemented.
-   Repository restored after rollback.
-   Failed rollbacks reported cleanly.
-   CLI supports `patch rollback`.
-   Tests pass.
-   Report records rollback operations.

## Definition of Done

An AI can safely undo a previously applied patch using the ECP protocol.
