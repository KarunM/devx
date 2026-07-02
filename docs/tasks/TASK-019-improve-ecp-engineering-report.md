# TASK-019: Improve ECP Engineering Report

**Status:** Completed  
**Completed:** 2026-07-02  
**Implemented By:** Codex  
**Verification:**
- `dotnet build`
- `dotnet test`
- `./ecp index .`
- `./ecp session create --format json`
- `./ecp search symbol Program --session <id> --format json`
- `./ecp report --session <id> --format json`
- `./ecp build --session <id> --format json`
- `./ecp test --session <id> --format json`
- `./ecp report --session <id> --format json`
**Phase:** Dogfooding & Benchmarking  
**Depends On:** TASK-017

## Objective

Improve `ecp report` so that it reports **only information observed by ECP**.

The report must be deterministic and must never estimate, infer, or fabricate metrics.

---

## Requirements

### Repository Metrics

Verify and correct:

- Indexed files
- Search operations
- Expanded files
- Snapshot requests
- Methods expanded
- Patches applied

These values must come directly from ECP session data.

---

### Build & Test Metrics

If build/test were executed through:

```bash
ecp build --session <id>
ecp test --session <id>
```

report:

- Build count
- Successful builds
- Failed builds
- Compiler warnings
- Compiler errors
- Test runs
- Passed
- Failed
- Skipped

If build/test were executed outside ECP (for example `dotnet build`), report:

```json
{
  "Available": false,
  "Reason": "Build/Test executed outside ECP."
}
```

Do not report misleading zero values.

---

### LLM Metrics

If ECP did not invoke the LLM directly, return:

```
unavailable
```

Do not estimate:

- Input tokens
- Output tokens
- Total tokens
- Cost
- Number of LLM requests

---

### Operation Log

Each operation must include:

- Operation
- Timestamp
- Duration
- Success
- Session ID

The log must be deterministic and ordered chronologically.

---

### Report Structure

The report must contain:

- Session Summary
- Operation Log
- Repository Metrics
- Build/Test Metrics
- LLM Metrics
- Efficiency Summary

Only include information that ECP actually knows.

---

## Validation

Run:

```bash
./ecp index .
./ecp session create --format json
./ecp search symbol Program --session <id> --format json
./ecp report --session <id> --format json
```

Also validate:

```bash
./ecp build --session <id> --format json
./ecp test --session <id> --format json
./ecp report --session <id> --format json
```

---

## Acceptance Criteria

- Report contains no inferred values.
- Repository metrics are accurate.
- Build/Test metrics distinguish observed vs external executions.
- LLM metrics are `unavailable` unless ECP owns provider communication.
- JSON output is deterministic.
- Existing tests pass.
- Add tests covering report generation.

---

## Definition of Done

`ecp report` becomes the authoritative engineering report for an ECP session, reporting only facts recorded by ECP itself.
