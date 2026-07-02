# TASK-027: Implement PATCH_VALIDATE

**Status:** Ready\
**Phase:** v0.2 -- Safe Editing\
**Depends On:** TASK-026

## Objective

Implement the `PATCH_VALIDATE` protocol operation to perform semantic
validation of a patch before it can be applied.

------------------------------------------------------------------------

## CLI

``` bash
./ecp patch validate <patch-file> --session <id> --format json
```

------------------------------------------------------------------------

## Validation Rules

Validate without modifying the repository.

Checks should include:

-   Patch schema is valid
-   Patch version is supported
-   Paths remain inside the repository root
-   Target files exist (or are allowed to be created)
-   Target symbols exist
-   No duplicate operations
-   No conflicting operations
-   Operations are applied in a valid order

------------------------------------------------------------------------

## Response

Return JSON similar to:

``` json
{
  "Valid": true,
  "Warnings": [],
  "Errors": [],
  "Files": 2,
  "Operations": 4
}
```

------------------------------------------------------------------------

## Session Behaviour

-   Record a `patch validate` operation.
-   Do not modify repository contents.
-   Update session metrics.

------------------------------------------------------------------------

## Implementation

1.  Add protocol models.
2.  Reuse the patch parser.
3.  Implement validation engine.
4.  Wire `ProtocolEngine`.
5.  Add CLI command.
6.  Add tests.
7.  Update report metrics.

------------------------------------------------------------------------

## Validation

``` bash
./ecp index .
SESSION_ID=$(./ecp session create --format json | jq -r '.Data.SessionId')

./ecp patch validate tests/patches/create-file.patch.json --session "$SESSION_ID" --format json

./ecp build --session "$SESSION_ID" --format json
./ecp test --session "$SESSION_ID" --format json
./ecp report --session "$SESSION_ID" --format json
```

------------------------------------------------------------------------

## Acceptance Criteria

-   `PATCH_VALIDATE` implemented.
-   Repository is unchanged.
-   Validation detects invalid patches.
-   Validation succeeds for valid patches.
-   CLI supports `patch validate`.
-   Tests pass.
-   Report records the validation operation.

------------------------------------------------------------------------

## Definition of Done

An AI can validate a patch before applying it, preventing invalid or
unsafe edits from reaching the repository.
