# SPEC-009B: Normalize Read Method and Read Type Responses

**Status:** Ready  
**Parent:** SPEC-009 – ECP Source Read API  
**Phase:** Source Read Consistency

## Objective

Make `ecp read method` and `ecp read type` return consistent source-read responses instead of simply delegating raw existing `GET_METHOD` and `GET_TYPE` responses.

## Current State

- `read method` delegates to existing method lookup.
- `read type` delegates to existing type lookup.
- The responses do not yet share the same source-read envelope as `read file`.

## Requirements

### Read Method

Command:

```bash
./ecp read method <method> --session <id> --format json
```

Return:

- read kind: `method`
- method id
- qualified name
- file path
- start line
- end line
- total lines in file
- returned lines
- source
- truncated flag

This may internally reuse `GET_METHOD`, but the external response should be normalized.

### Read Type

Command:

```bash
./ecp read type <type> --session <id> --format json
```

Default behavior:

- return type metadata only
- do not return full type source unless `--source` is provided later

For now, normalize the response envelope.

Return:

- read kind: `type`
- type id
- qualified name
- file path
- metadata
- source returned: false

Optional future:

```bash
./ecp read type <type> --source --session <id> --format json
```

## Response Examples

### Method

```json
{
  "Kind": "method",
  "SymbolId": "...",
  "QualifiedName": "Ecp.Protocol.ProtocolEngine.HandlePatchAsync",
  "Path": "src/Ecp.Protocol/ProtocolEngine.cs",
  "StartLine": 120,
  "EndLine": 180,
  "ReturnedLines": 61,
  "Truncated": false,
  "Source": "..."
}
```

### Type

```json
{
  "Kind": "type",
  "SymbolId": "...",
  "QualifiedName": "Ecp.Protocol.ProtocolEngine",
  "Path": "src/Ecp.Protocol/ProtocolEngine.cs",
  "SourceReturned": false,
  "Metadata": {}
}
```

## Metrics

Record:

- read method count
- read type count
- source lines returned
- symbols read

## Validation

```bash
./ecp read method HandlePatchAsync --session <id> --format json
./ecp read type ProtocolEngine --session <id> --format json
./ecp build --session <id> --format json
./ecp test --session <id> --format json
./ecp report --session <id> --format json
```

## Acceptance Criteria

- `read method` returns normalized read-source response.
- `read type` returns normalized read-source response.
- Existing `GET_METHOD` and `GET_TYPE` still work.
- Metrics record read-method/read-type operations.
- Build and tests pass.
