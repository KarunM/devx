# TASK-020: Implement GET_METHOD_SUMMARY

**Status:** Completed  
**Completed:** 2026-07-02  
**Implemented By:** Codex  
**Verification:**
- `./devx build --session d5ff50dc-3e57-47b0-9959-83883ed58637 --format json`
- `./devx test --session d5ff50dc-3e57-47b0-9959-83883ed58637 --format json`
- `./devx method summary Run --session d5ff50dc-3e57-47b0-9959-83883ed58637 --format json`
**Phase:** v0.2 – Context Expansion  
**Depends On:** TASK-016, TASK-019

---

## Objective

Implement the `GET_METHOD_SUMMARY` protocol operation.

This operation returns a **metadata-only summary** of a method so an LLM can understand a method's purpose, signature, size, and relationships before requesting implementation source.

It must not return method source code.

---

## Motivation

DevX currently supports repository search and file snapshots.

The next context expansion step is method-level discovery:

```text
SEARCH_SYMBOL
    ↓
GET_FILE_SNAPSHOT
    ↓
GET_METHOD_SUMMARY
    ↓
GET_METHOD
```

`GET_METHOD_SUMMARY` helps reduce token usage by allowing the LLM to inspect method metadata without receiving full implementation code.

---

## Protocol Operation

```text
GET_METHOD_SUMMARY
```

---

## CLI Command

Add:

```bash
devx method summary <method> --session <session-id> --format json
```

Optional supported forms:

```bash
devx method summary <method-id> --session <session-id> --format json
devx method summary <qualified-method-name> --session <session-id> --format json
```

---

## Inputs

The operation should accept one of:

- Method ID
- Method name
- Qualified method name
- Optional file path to disambiguate
- Optional containing type to disambiguate

Example protocol payload:

```json
{
  "method": "HandleAsync",
  "file": "src/DevX.Protocol/ProtocolEngine.cs",
  "type": "ProtocolEngine"
}
```

---

## Output

Return metadata only.

The response should include, where available:

- Method ID
- Method name
- Qualified name
- Containing type
- File path
- Project
- Namespace
- Signature
- Parameters
- Return type
- Visibility
- Approximate line count
- Start line
- End line
- Whether the method is constructor/static/async
- Known callers, if indexed
- Known callees, if indexed
- Related tests, if indexed

Do **not** return method source code.

---

## Example Response

```json
{
  "methodId": "...",
  "name": "HandleAsync",
  "qualifiedName": "DevX.Protocol.ProtocolEngine.HandleAsync",
  "containingType": "ProtocolEngine",
  "filePath": "src/DevX.Protocol/ProtocolEngine.cs",
  "project": "DevX.Protocol",
  "namespace": "DevX.Protocol",
  "signature": "Task<ProtocolResponse> HandleAsync(ProtocolRequest request, CancellationToken cancellationToken)",
  "parameters": [
    {
      "name": "request",
      "type": "ProtocolRequest"
    },
    {
      "name": "cancellationToken",
      "type": "CancellationToken"
    }
  ],
  "returnType": "Task<ProtocolResponse>",
  "visibility": "public",
  "approximateLineCount": 42,
  "startLine": 18,
  "endLine": 60,
  "isConstructor": false,
  "isStatic": false,
  "isAsync": true,
  "callers": [],
  "callees": [],
  "relatedTests": []
}
```

---

## Session Behavior

When a method summary is requested:

- Record the operation in the session metrics ledger.
- Add the method to the session working set.
- Mark the method as summarized, but not expanded.
- Do not mark the method as source-expanded unless `GET_METHOD` is later called.

---

## Implementation Steps

1. Add method summary request/response models.
2. Extend the domain/storage contract to retrieve method metadata.
3. Add SQLite query support for method summary lookup.
4. Handle ambiguity:
   - if multiple methods match, return a structured ambiguity error with candidates.
5. Add `GET_METHOD_SUMMARY` handling to the ProtocolEngine.
6. Add CLI command:
   - `devx method summary ...`
7. Update session metrics/working set.
8. Add tests for:
   - method lookup by exact name
   - method lookup by qualified name
   - method lookup with file disambiguation
   - ambiguous method names
   - no source code returned
   - session state updated
9. Run validation.

---

## Error Handling

Return structured errors for:

- Method not found
- Ambiguous method match
- Invalid session
- Repository not indexed
- Unsupported payload
- Storage/query failure

Ambiguous method responses should include candidate metadata, but not source code.

---

## Acceptance Criteria

- `GET_METHOD_SUMMARY` works through the ProtocolEngine.
- CLI supports `devx method summary`.
- Method summaries return metadata only.
- No implementation source code is returned.
- Ambiguous methods are handled with structured errors.
- Session working set is updated.
- DevX metrics record the operation.
- `dotnet build` succeeds.
- `dotnet test` succeeds.
- `./devx report --session <id> --format json` reflects the operation.

---

## Verification Commands

```bash
./devx index .
SESSION_ID=$(./devx session create --format json | jq -r '.Data.SessionId')

./devx search symbol ProtocolEngine --session "$SESSION_ID" --format json
./devx snapshot src/DevX.Protocol/ProtocolEngine.cs --session "$SESSION_ID" --format json
./devx method summary HandleAsync --session "$SESSION_ID" --format json

./devx build --session "$SESSION_ID" --format json
./devx test --session "$SESSION_ID" --format json
./devx report --session "$SESSION_ID" --format json
```

If `jq` is unavailable, copy the session ID manually.

---

## Out of Scope

- Returning method implementation source
- Full call graph generation
- Semantic summaries generated by an LLM
- Test impact analysis
- Cross-language method summaries

These belong to later tasks.

---

## Definition of Done

An AI client can request a method summary through DevX and receive enough metadata to decide whether it needs to request the full method implementation later.

This completes the Level 2 method-level metadata step in the progressive context retrieval workflow.
