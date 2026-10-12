# SPEC-009A: Read File Full Mode and Truncation Controls

**Status:** Ready  
**Parent:** SPEC-009 – DevX Source Read API  
**Phase:** Source Read Hardening

## Objective

Extend `devx read file` with explicit full-file reads and safe truncation controls.

The goal is to make source reads useful while preventing accidental large context expansion.

## Current State

`devx read file` supports bounded line ranges.

Example:

```bash
./devx read file src/DevX.Cli/Program.cs --range 1:120 --session <id> --format json
```

Missing:

- explicit `--full`
- truncation controls
- max line limits
- clear metadata when output is truncated

## Requirements

### Range Reads

Continue supporting:

```bash
./devx read file <path> --range <start>:<end> --session <id> --format json
```

### Full Reads

Add explicit full-file mode:

```bash
./devx read file <path> --full --session <id> --format json
```

Full reads must never happen implicitly.

### Default Behavior

If no `--range` and no `--full` is provided, return an error:

```json
{
  "Success": false,
  "Errors": [
    {
      "Code": "SourceRangeRequired",
      "Message": "Use --range or --full explicitly."
    }
  ]
}
```

### Truncation

Add a safe maximum line limit.

Suggested default:

```text
MaxLines = 300
```

If output exceeds the limit:

```json
{
  "Truncated": true,
  "ReturnedLines": 300,
  "TotalLines": 640
}
```

`Truncated` is true only when the returned source is shorter than the requested span. For an
element read, compare against the indexed element span; for a ranged file read, compare against
the requested range after clipping it to the file. `TotalLines` continues to describe the whole
file. A fully returned method or type therefore has `Truncated: false` even when its file has
additional lines.

Optional later flag:

```bash
--max-lines 500
```

## Response Shape

```json
{
  "Path": "src/DevX.Cli/Program.cs",
  "Language": "C#",
  "StartLine": 1,
  "EndLine": 120,
  "TotalLines": 640,
  "ReturnedLines": 120,
  "Truncated": false,
  "Source": "..."
}
```

## Safety Rules

- Reject paths outside repository root.
- Reject binary files.
- Reject generated/build directories unless explicitly allowed.
- Record full-file reads separately in metrics.
- Record total lines returned.

## Metrics

Update session/report metrics with:

- read file count
- read range count
- read full count
- source lines returned
- truncated reads

## Validation

```bash
./devx read file src/DevX.Cli/Program.cs --range 1:20 --session <id> --format json
./devx read file src/DevX.Cli/Program.cs --full --session <id> --format json
./devx read file src/DevX.Cli/Program.cs --session <id> --format json
./devx build --session <id> --format json
./devx test --session <id> --format json
./devx report --session <id> --format json
```

## Acceptance Criteria

- Full-file reads require `--full`.
- Missing range/full returns a clear error.
- Large reads are truncated safely.
- Metrics record range/full/truncated reads.
- Build and tests pass.
