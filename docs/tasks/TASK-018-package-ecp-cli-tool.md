# TASK-018: Package ECP CLI as a Local/Global Tool

**Status:** Ready  
**Phase:** Dogfooding & Developer Experience  
**Depends On:** TASK-017

---

## Objective

Package the ECP CLI so agents and developers can invoke:

```bash
ecp ...
```

instead of:

```bash
dotnet run --project src/Ecp.Cli -- ...
```

This reduces coupling between external agents and the repository implementation layout.

---

## Motivation

During dogfooding, agents currently need to know the internal CLI project path:

```bash
dotnet run --project src/Ecp.Cli -- index .
```

That leaks implementation structure and makes ECP feel like a development artifact rather than a tool.

The desired interface is:

```bash
ecp index .
ecp session create --format json
ecp search symbol ProtocolEngine --session <id> --format json
ecp report --session <id> --format json
```

---

## Scope

Implement packaging support for the CLI as either:

1. a .NET local tool, or
2. a .NET global tool, or
3. a local development shim script.

The preferred MVP path is a **local tool/shim** so contributors and agents can use `ecp` consistently inside the repository.

---

## Requirements

The implementation should support:

```bash
ecp index .
ecp session create --format json
ecp search symbol <query> --session <id> --format json
ecp search text <query> --session <id> --format json
ecp snapshot <file> --session <id> --format json
ecp patch apply <patch-file> --session <id>
ecp build --session <id> --format json
ecp test --session <id> --format json
ecp diagnostics --session <id> --format json
ecp report --session <id> --format json
```

---

## Implementation Options

### Option A: .NET Local Tool

Configure the CLI project for tool packaging.

Add package metadata to `src/Ecp.Cli/Ecp.Cli.csproj`.

Example properties:

```xml
<PackAsTool>true</PackAsTool>
<ToolCommandName>ecp</ToolCommandName>
<PackageId>Ecp.Cli</PackageId>
```

Then validate:

```bash
dotnet pack src/Ecp.Cli
dotnet tool install --tool-path ./.tools --add-source src/Ecp.Cli/bin/Debug Ecp.Cli
./.tools/ecp --help
```

### Option B: Repository Shim

Create a small executable script:

```bash
./ecp
```

that forwards to:

```bash
dotnet run --project src/Ecp.Cli -- "$@"
```

This is acceptable for MVP dogfooding.

### Option C: Global Tool

Package and install globally:

```bash
dotnet tool install --global Ecp.Cli
```

This is useful later, but not required for MVP dogfooding.

---

## Acceptance Criteria

- `ecp` can be invoked from the repository root.
- `ecp index .` works.
- `ecp session create --format json` works.
- `ecp report --session <id> --format json` works.
- Existing `dotnet build` passes.
- Existing `dotnet test` passes.
- Documentation explains how to install or invoke the CLI.

---

## Verification Commands

```bash
dotnet build
dotnet test

ecp index .
SESSION_ID=$(ecp session create --format json | jq -r '.Data.SessionId')
ecp search symbol ProtocolEngine --session "$SESSION_ID" --format json
ecp report --session "$SESSION_ID" --format json
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

Agents and developers can use `ecp ...` directly without knowing that the CLI lives at `src/Ecp.Cli`.

This makes ECP usable as a real command-line tool during dogfooding.
