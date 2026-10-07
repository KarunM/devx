# TASK-029: Implement Incremental Indexing

**Status:** Completed\
**Phase:** v0.3 -- Repository Performance

## Objective

Avoid rebuilding the repository index when nothing has changed.

## CLI

``` bash
./devx index .
./devx index . --incremental
./devx index status
```

## Requirements

-   Detect unchanged repositories.
-   Index only changed/new/deleted files.
-   Store repository fingerprint (timestamps/hash/version).
-   Report whether indexing was full or incremental.
-   Preserve existing repository IDs.
-   Record indexing metrics.

## Response

``` json
{
  "Mode":"Incremental",
  "FilesScanned":12,
  "FilesIndexed":3,
  "FilesSkipped":9,
  "DurationMs":182
}
```

## Implementation

1.  Persist repository fingerprint.
2.  Detect changed files.
3.  Update only affected symbols.
4.  Add `index status`.
5.  Update metrics/report.
6.  Add tests for full and incremental indexing.

## Validation

``` bash
./devx index .
./devx index --incremental .
./devx index status
./devx build --session <id> --format json
./devx test --session <id> --format json
./devx report --session <id> --format json
```

## Acceptance Criteria

-   Unchanged repositories are not fully reindexed.
-   Changed files are reindexed correctly.
-   Deleted files are removed from the index.
-   Existing search results remain correct.
-   Report distinguishes full vs incremental indexing.
