# TASK-018: Package DevX CLI as a Local/Global Tool

**Status:** Ready  
**Phase:** Dogfooding & Developer Experience  
**Depends On:** TASK-017

---

## Objective

Package the DevX CLI so agents and developers can invoke:

```bash
devx ...
```

instead of:

```bash
dotnet run --project src/DevX.Cli -- ...
```

This reduces coupling between external agents and the repository implementation layout.

---

## Motivation

During dogfooding, agents currently need to know the internal CLI project path:

```bash
dotnet run --project src/DevX.Cli -- index .
```

That leaks implementation structure and makes DevX feel like a development artifact rather than a tool.

The desired interface is:

```bash
devx index .
devx session create --format json
devx search symbol ProtocolEngine --session <id> --format json
devx report --session <id> --format json
```

---

## Scope

Implement packaging support for the CLI as either:

1. a .NET local tool, or
2. a .NET global tool, or
3. a local development shim script.

The preferred MVP path is a **local tool/shim** so contributors and agents can use `devx` consistently inside the repository.

---

## Requirements

The implementation should support:

```bash
devx index .
devx session create --format json
devx search symbol <query> --session <id> --format json
devx search text <query> --session <id> --format json
devx snapshot <file> --session <id> --format json
devx patch apply <patch-file> --session <id>
devx build --session <id> --format json
devx test --session <id> --format json
devx diagnostics --session <id> --format json
devx report --session <id> --format json
```

---

## Implementation Options

### Option A: .NET Local Tool

Configure the CLI project for tool packaging.

Add package metadata to `src/DevX.Cli/DevX.Cli.csproj`.

Example properties:

```xml
<PackAsTool>true</PackAsTool>
<ToolCommandName>devx</ToolCommandName>
<PackageId>DevX.Cli</PackageId>
```

Then validate:

```bash
dotnet pack src/DevX.Cli
dotnet tool install --tool-path ./.tools --add-source src/DevX.Cli/bin/Debug DevX.Cli
./.tools/devx --help
```

### Option B: Repository Shim

Create a small executable script:

```bash
./devx
```

that forwards to:

```bash
dotnet run --project src/DevX.Cli -- "$@"
```

This is acceptable for MVP dogfooding.

### Option C: Global Tool

Package and install globally:

```bash
dotnet tool install --global DevX.Cli
```

This is useful later, but not required for MVP dogfooding.

---

## Acceptance Criteria

- `devx` can be invoked from the repository root.
- `devx index .` works.
- `devx session create --format json` works.
- `devx report --session <id> --format json` works.
- Existing `dotnet build` passes.
- Existing `dotnet test` passes.
- Documentation explains how to install or invoke the CLI.

---

## Verification Commands

```bash
dotnet build
dotnet test

devx index .
SESSION_ID=$(devx session create --format json | jq -r '.Data.SessionId')
devx search symbol ProtocolEngine --session "$SESSION_ID" --format json
devx report --session "$SESSION_ID" --format json
```

If `jq` is unavailable, extract the session id manually from the JSON output.

---

## Out of Scope

- Publishing to NuGet
- CI release pipeline
- Signed binaries
- Cross-platform installers
- Homebrew/winget packaging

---

## Definition of Done

Agents and developers can use `devx ...` directly without knowing that the CLI lives at `src/DevX.Cli`.

This makes DevX usable as a real command-line tool during dogfooding.
