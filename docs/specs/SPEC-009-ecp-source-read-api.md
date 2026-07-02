# SPEC-009: ECP Source Read API

**Status:** Phase 1 Complete  
**Phase:** Capability Specification  
**Purpose:** Reduce direct IDE/file reads by exposing controlled source reads through ECP.

---

## Objective

Define an explicit ECP source-read capability so agents can retrieve repository source through ECP instead of using IDE file reads, `cat`, `grep`, or direct filesystem access.

This does **not** replace existing ECP context operations.

It complements them.

---

## Existing Capabilities

ECP already supports progressive context retrieval:

```text
SEARCH
→ FILE SNAPSHOT
→ TYPE
→ METHOD SUMMARY
→ METHOD SOURCE
```

These should remain the preferred flow.

---

## Why Source Read Is Still Needed

`GET_FILE_SNAPSHOT` is metadata-only.

It answers:

```text
What is in this file?
```

It should not return source code.

`GET_METHOD` returns one method only.

It answers:

```text
Show me this specific method.
```

But agents sometimes need small source regions that are not a full method, such as:

- using/import section
- namespace declaration
- CLI switch/dispatch block
- record/model declarations
- property groups
- surrounding context around an insertion point
- test setup blocks
- small line ranges for diagnostics
- non-method source regions

Without an ECP source-read command, agents fall back to direct file reads.

This spec provides a controlled alternative.

---

## Principle

Agents should not read source files directly when ECP can provide the required source.

Preferred order:

```text
SEARCH
→ SNAPSHOT
→ TYPE
→ METHOD SUMMARY
→ METHOD
→ READ RANGE
→ READ FILE
```

Full-file reads should be allowed but discouraged.

They must be explicit and recorded.

---

## Commands

### Read File Metadata + Optional Source

```bash
./ecp read file <path> --session <id> --format json
```

By default, this should either:

- refuse full-file source unless `--full` is provided, or
- return source only up to a safe size limit.

Recommended safer form:

```bash
./ecp read file <path> --range 1:120 --session <id> --format json
```

---

### Read Line Range

```bash
./ecp read file <path> --range <start>:<end> --session <id> --format json
```

Example:

```bash
./ecp read file src/Ecp.Cli/Program.cs --range 1:160 --session <id> --format json
```

Response includes:

- path
- language
- start line
- end line
- total lines
- source
- truncated flag

---

### Read Method

```bash
./ecp read method <method> --session <id> --format json
```

This may delegate to existing `GET_METHOD`.

---

### Read Type

```bash
./ecp read type <type> --session <id> --format json
```

This should not return the full type source by default.

Recommended behavior:

- return type metadata
- optionally allow `--source` later

---

## Safety Rules

- All paths must remain inside repository root.
- File reads must be recorded in session metrics.
- Full-file reads must be explicit.
- Large reads should be truncated unless `--full` is provided.
- Binary files must be rejected.
- Generated/build directories should be blocked unless explicitly allowed.

---

## Metrics

Record:

- read file count
- read range count
- read method count
- read type count
- full-file read count
- total lines returned
- truncated reads

These metrics should appear in `ecp report`.

---

## Response Example

```json
{
  "Path": "src/Ecp.Cli/Program.cs",
  "Language": "C#",
  "StartLine": 1,
  "EndLine": 120,
  "TotalLines": 640,
  "Truncated": false,
  "Source": "..."
}
```

---

## Acceptance Criteria

- Agents can read line ranges through ECP.
- Agents no longer need direct IDE reads for small source regions.
- Full-file reads are explicit and recorded.
- Source reads appear in session metrics.
- Existing snapshot/type/method behavior remains unchanged.
- Build and tests pass.

---

## Non-Goals

- Replacing `GET_FILE_SNAPSHOT`
- Replacing `GET_METHOD`
- Returning entire repositories
- Semantic editing
- Patch application

---

## Definition of Done

An agent can retrieve the exact source region it needs through ECP, while ECP records and limits that source expansion.
