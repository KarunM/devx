# TASK-019: Improve DevX Engineering Report

**Status:** Completed  
**Completed:** 2026-07-02  
**Implemented By:** Codex  
**Verification:**
- `dotnet build`
- `dotnet test`
- `./devx index .`
- `./devx session create --format json`
- `./devx search symbol Program --session <id> --format json`
- `./devx report --session <id> --format json`
- `./devx build --session <id> --format json`
- `./devx test --session <id> --format json`
- `./devx report --session <id> --format json`
**Phase:** Dogfooding & Benchmarking  
**Depends On:** TASK-017

## Objective

Improve `devx report` so that it reports **only information observed by DevX**.

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

These values must come directly from DevX session data.

---

### Build & Test Metrics

If build/test were executed through:

```bash
devx build --session <id>
devx test --session <id>
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

If build/test were executed outside DevX (for example `dotnet build`), report:

```json
{
  "Available": false,
  "Reason": "Build/Test executed outside DevX."
}
```

Do not report misleading zero values.

---

### LLM Metrics

If DevX did not invoke the LLM directly, return:

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

Only include information that DevX actually knows.

---

## Validation

Run:

```bash
./devx index .
./devx session create --format json
./devx search symbol Program --session <id> --format json
./devx report --session <id> --format json
```

Also validate:

```bash
./devx build --session <id> --format json
./devx test --session <id> --format json
./devx report --session <id> --format json
```

---

## Acceptance Criteria

- Report contains no inferred values.
- Repository metrics are accurate.
- Build/Test metrics distinguish observed vs external executions.
- LLM metrics are `unavailable` unless DevX owns provider communication.
- JSON output is deterministic.
- Existing tests pass.
- Add tests covering report generation.

---

## Definition of Done

`devx report` becomes the authoritative engineering report for an DevX session, reporting only facts recorded by DevX itself.
