# TASK-017: Implement DevX Session Metrics & Usage Report

**Status:** Ready  
**Phase:** Dogfooding & Benchmarking

## Objective

Implement an DevX metrics subsystem that records engineering activity for every session.

The metrics must be collected by the CLI itself, not inferred by the LLM.

---

## Motivation

DevX should provide measurable evidence that it reduces repository exploration and LLM context usage.

Every engineering session should end with a reproducible usage report.

---

## Scope

Collect metrics for:

### DevX Operations

- index
- session create
- search symbol
- search text
- snapshot
- patch
- build
- test
- diagnostics

For each operation record:

- operation name
- timestamp
- duration
- success/failure
- session id

---

### Repository Metrics

Track:

- indexed files
- searched files
- expanded files
- snapshots requested
- methods expanded
- patches applied

---

### Build Metrics

Track:

- build count
- successful builds
- failed builds
- warnings
- errors

---

### Test Metrics

Track:

- test runs
- passed
- failed
- skipped

---

### LLM Metrics

If available from the provider:

- provider
- model
- input tokens
- output tokens
- reasoning tokens
- total tokens
- latency
- estimated cost

If unavailable, record:

- unavailable

---

## CLI

Add:

```bash
devx report --session <session-id>
devx report --session <session-id> --format json
```

---

## Output

Produce a report including:

- Session Summary
- DevX Operation Log
- Repository Metrics
- Build/Test Metrics
- LLM Metrics
- Efficiency Summary

---

## Acceptance Criteria

- Metrics persist per session.
- Report can be generated at any time.
- JSON output is deterministic.
- Existing functionality continues to pass.

---

## Verification

Run:

```bash
dotnet build
dotnet test

devx report --session <id>
```

---

## Definition of Done

DevX can generate a complete engineering usage report without requiring the LLM to estimate or reconstruct session activity.
