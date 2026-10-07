# DevX Runtime Build/Test Discovery — Implementation Plan

## 1. Problem Statement

`DotnetBuildTestService` is hardcoded to `dotnet build` / `dotnet test` and only discovers `.sln` / `.csproj` files. This makes DevX unusable for:
- Node.js / npm / pnpm / yarn
- Python / poetry / pytest
- Rust / Cargo
- Go / Make / CMake
- Java / Maven / Gradle
- Any custom or monorepo build setup

## 2. Goals

1. **Runtime discovery**: Detect build/test tooling from project files without user intervention.
2. **Language agnostic**: Support any project type via a plugin-style discovery map.
3. **Configurable**: Allow explicit `.devx/config.json` overrides when auto-discovery is insufficient.
4. **Pluggable diagnostics**: Parse build errors/warnings from any tool (MSBuild, ESLint, cargo, gcc, etc.).
5. **Backward compatible**: Existing .NET repos continue to work unchanged.

## 3. Proposed Architecture

### 3.1 Configuration Layer (`.devx/config.json`)

Yes — DevX **should** read a per-repository configuration file. Auto-discovery is a best-effort default; real-world repos need overrides.

**Default config file**: `.devx/config.json` (auto-created on first `devx index` if absent)

**Schema**:
```json
{
  "version": "1.0",
  "projectType": "auto",
  "build": {
    "command": "dotnet",
    "args": ["build", "DevX.sln"],
    "workingDirectory": ".",
    "env": null
  },
  "test": {
    "command": "dotnet",
    "args": ["test", "DevX.sln", "--verbosity", "normal"],
    "workingDirectory": ".",
    "env": null
  },
  "diagnostics": {
    "parser": "msbuild"
  }
}
```

- `projectType: "auto"` → discovery engine scans repo.
- `projectType: "custom"` → use explicit `build` / `test` blocks.
- If `.devx/config.json` is missing, discovery runs and writes the inferred config.

### 3.1a Custom Commands — Deep Dive

Custom commands are the **primary escape hatch** when auto-discovery fails. The `build` and `test` blocks accept any executable on `PATH` plus arbitrary arguments.

**Example — Shell script:**
```json
{
  "version": "1.0",
  "projectType": "custom",
  "build": {
    "command": "./scripts/build.sh",
    "args": ["--release", "--parallel"],
    "workingDirectory": "."
  },
  "test": {
    "command": "./scripts/test.sh",
    "args": ["--coverage", "--junit"],
    "workingDirectory": "."
  },
  "diagnostics": { "parser": "generic" }
}
```

**Example — Python project with custom workflow:**
```json
{
  "version": "1.0",
  "projectType": "custom",
  "build": {
    "command": "python",
    "args": ["-m", "compileall", "src/"],
    "workingDirectory": "."
  },
  "test": {
    "command": "pytest",
    "args": ["tests/", "-x", "--tb=short", "-q"],
    "workingDirectory": "."
  },
  "diagnostics": { "parser": "pytest" }
}
```

**Example — Bazel monorepo:**
```json
{
  "version": "1.0",
  "projectType": "custom",
  "build": {
    "command": "bazel",
    "args": ["build", "//src/..."],
    "workingDirectory": "."
  },
  "test": {
    "command": "bazel",
    "args": ["test", "//src/...", "//tests/..."],
    "workingDirectory": "."
  },
  "diagnostics": { "parser": "generic" }
}
```

**Example — Docker-based build:**
```json
{
  "version": "1.0",
  "projectType": "custom",
  "build": {
    "command": "docker",
    "args": ["build", "-t", "myapp", "."],
    "workingDirectory": "."
  },
  "test": {
    "command": "docker",
    "args": ["run", "--rm", "myapp", "test"],
    "workingDirectory": "."
  },
  "diagnostics": { "parser": "generic" }
}
```

**Example — Make with environment variable:**
```json
{
  "version": "1.0",
  "projectType": "custom",
  "build": {
    "command": "make",
    "args": ["all"],
    "workingDirectory": ".",
    "env": { "CC": "clang", "CFLAGS": "-O2 -Wall" }
  },
  "test": {
    "command": "make",
    "args": ["test"],
    "workingDirectory": "."
  },
  "diagnostics": { "parser": "gcc" }
}
```

#### BuildConfig Schema

The `BuildConfig` record has four fields:

| Field | Type | Description |
|---|---|---|
| `command` | `string` | Executable name or path. Can be `"dotnet"`, `"./build.sh"`, `"docker"`, `"python"`, etc. Must be on `PATH` or an absolute/relative path. |
| `args` | `string[]` | Arguments passed to the command. No shell interpolation — each arg is passed as-is. |
| `workingDirectory` | `string` | Working directory relative to repo root. `"."` means repo root. `"src/frontend"` for sub-project builds. |
| `env` | `Dictionary<string, string>?` | Optional environment variables injected before the command runs. |

**Important: No shell interpolation.** `args` are passed as a list, not parsed as a shell string. This means `"args": ["|", "grep", "error"]` will NOT pipe — it will pass literal `"|"` as an argument to the command. If you need pipes, redirects, or `&&` chains, write a shell script and point `command` at it.

**Composite commands:** If your build requires multiple steps (`npm install && npm run build && npm run typecheck`), you have three options:

1. **Shell script** (recommended): `command: "./scripts/build.sh"` — the script contains all steps.
2. **Make / Just**: `command: "just"`, `args: ["build"]` — the `justfile` defines the multi-step recipe.
3. **DevX multi-step** (future v2): `"build.steps": [{"command": "npm", "args": ["install"]}, {"command": "npm", "args": ["run", "build"]}]` — out of scope for v1.

#### Unknown Project Type — What Happens When Discovery Fails?

When the discovery engine finds **no recognizable config files**:

1. **If `.devx/config.json` exists**: Use it (even if `projectType` is unknown — the user knows best).
2. **If no config exists**: Discovery writes a "placeholder" config:
   ```json
   {
     "version": "1.0",
     "projectType": "unknown",
     "build": null,
     "test": null,
     "diagnostics": { "parser": "generic" }
   }
   ```
   DevX prints a warning: `No build system detected. Create .devx/config.json or run devx config --wizard.`
   `devx build` and `devx test` return a helpful error with a link to docs.

#### Command Resolution Strategy

When `BuildTestService` runs a command, it resolves the executable in this order:

1. **Absolute path** (`/usr/local/bin/custom-tool`) → use as-is
2. **Relative path** (`./scripts/build.sh`) → resolve relative to repo root
3. **Bare name** (`dotnet`, `cargo`) → search `PATH` via `Environment.GetEnvironmentVariable("PATH")`
4. **Not found** → return `ExecutionRecord` with `Success: false` and error message

### 3.2 Discovery Engine (`BuildSystemDiscoveryService`)

Scans the repository root for known config files and maps them to a `BuildSystemProfile`:

| Config File(s) | Detected Type | Build Command | Test Command | Diagnostic Parser |
|---|---|---|---|---|
| `*.sln`, `*.csproj` | dotnet | `dotnet build <sln>` | `dotnet test <sln>` | `msbuild` |
| `package.json` | npm | `npm run build` | `npm test` | `npm` / `jest` / `eslint` |
| `Cargo.toml` | rust | `cargo build` | `cargo test` | `cargo` |
| `pom.xml` | maven | `mvn compile` | `mvn test` | `maven` |
| `build.gradle` | gradle | `gradle build` | `gradle test` | `gradle` |
| `go.mod` | go | `go build ./...` | `go test ./...` | `go` |
| `Makefile` | make | `make` | `make test` | `gcc` / `generic` |
| `CMakeLists.txt` | cmake | `cmake --build build` | `ctest` | `gcc` / `generic` |
| `pyproject.toml` | python | `python -m build` | `pytest` | `pytest` / `flake8` |
| `WORKSPACE` / `BUILD` | bazel | `bazel build //...` | `bazel test //...` | `generic` |
| `justfile` / `Justfile` | just | `just build` | `just test` | `generic` |
| `taskfile.yml` | task | `task build` | `task test` | `generic` |

Discovery algorithm:
1. Walk repo root (1 level deep, then `src/`, `tests/`, `apps/`)
2. Score each candidate by file count (e.g., 5 `.csproj` = strong .NET signal)
3. Pick highest-confidence match
4. Check that the command exists on `PATH` (`which dotnet`, `which cargo`, etc.)
5. Write inferred config to `.devx/config.json`

### 3.3 Replace `DotnetBuildTestService` → `BuildTestService`

```csharp
public interface IBuildTestService
{
    Task<ExecutionRecord> RunBuildAsync(string repositoryRoot, CancellationToken ct);
    Task<ExecutionRecord> RunTestsAsync(string repositoryRoot, CancellationToken ct);
}

public sealed class BuildTestService : IBuildTestService
{
    private readonly BuildSystemDiscoveryService _discovery;

    public async Task<ExecutionRecord> RunBuildAsync(string repositoryRoot, CancellationToken ct)
    {
        var config = await _discovery.GetConfigAsync(repositoryRoot);
        if (config.Build is null)
        {
            return ExecutionRecord.Failed("No build command configured. Run 'devx config' to set up.");
        }
        return await RunCommandAsync(config.Build, repositoryRoot, ct);
    }
}
```

### 3.4 Pluggable Diagnostic Parser

Each parser converts raw tool output into `DiagnosticRecord` (Severity, Code, Message, File, Line, Column):

| Parser | Regex / Pattern | Example |
|---|---|---|
| `msbuild` | `^(.*)\((\d+),(\d+)\):\s*(error|warning)\s*(\w+\d*):\s*(.*)$` | `Program.cs(15,5): error CS0103: The name...` |
| `cargo` | `^error\[?(\w*)\]?:(.*)\s+-->\s+(.*):(\d+):(\d+)` | `error[E0425]: unresolved name --> src/main.rs:3:5` |
| `gcc` | `^(.*):(\d+):(\d+):\s*(error|warning):\s*(.*)$` | `main.c:10:5: error: unknown type...` |
| `npm` | JSON parsing for `npm test -- --json` | Jest / ESLint JSON reporters |
| `pytest` | Parse `--tb=short` or JSON output | `test_foo.py::test_bar FAILED` |
| `go` | `^(.*):(\d+):\d+:\s*(.*)$` | `main.go:10:5: undefined: fmt` |
| `generic` | Line-by-line fallback; flag lines with `error`/`warning` | Best-effort for unknown tools |

### 3.5 Protocol & CLI Changes

- `ProtocolEngine` passes `repositoryRoot` to `BuildTestService`.
- `BuildTestService` loads `.devx/config.json` or discovers.
- `devx build` and `devx test` stay the same from a user perspective.
- New CLI: `devx config` (or `devx build config`) to show inferred config and allow manual editing.
- New CLI: `devx config --wizard` — interactive prompt to generate config.

### 3.6 Report Composer Updates

`DevXReportComposer` currently hardcodes `metric.Operation == "build"` and `"test"`. Update to:
- Read operation names from config
- Support arbitrary operation names (e.g., `lint`, `typecheck`, `format-check`)

### 3.7 Storage Changes

Minimal — `ExecutionRecord` and `DiagnosticRecord` are already generic enough. May need:
- Add `Command` field to `ExecutionRecord` (currently `RawOutput` only, no command name)
- Add `Parser` field to `DiagnosticRecord` for traceability

## 4. Implementation Phases

| Phase | Scope | Files | Est. Effort |
|---|---|---|---|
| **1. Config + Discovery** | `.devx/config.json` schema, `BuildSystemDiscoveryService`, detection map | New: `BuildSystemDiscoveryService.cs`, `BuildConfig.cs` | Medium |
| **2. Generic BuildTestService** | Replace `DotnetBuildTestService`, keep backward compat | `DotnetBuildTestService.cs` → `BuildTestService.cs`, `ProtocolEngine.cs` | Medium |
| **3. Diagnostic Parsers** | `IDiagnosticParser` + `MsBuildParser`, `CargoParser`, `GenericParser` | New: `DiagnosticParsers/` directory | Medium |
| **4. CLI & Protocol** | `devx config` command, `config` protocol op, update capabilities | `Program.cs`, `ProtocolModels.cs`, `ProtocolEngine.cs` | Small |
| **5. Tests** | Discovery tests, parser tests, cross-toolchain integration tests | `tests/DevX.Tests/Build/` | Medium |
| **6. Report Metrics** | Generic operation tracking, update `DevXReportComposer` | `DevXReport.cs`, `DevXReportComposer.cs` | Small |

## 5. Open Questions

1. **Should discovery be cached?** Yes — write `.devx/config.json` after first `devx index` so subsequent `build`/`test` calls skip scanning.
2. **Monorepo support?** Config can support multiple profiles: `build.frontend`, `build.backend`, etc. Out of scope for v1; future work.
3. **Should the user edit `config.json` or via CLI?** Both — `devx config` shows current, `devx config set build.command "npm run build"` edits.
4. **What about Docker / remote build?** Future — add `build.remote: { dockerfile, image }` to config schema later.

## 6. Files to Create / Modify

**New files:**
- `src/DevX.Indexer/BuildSystemDiscoveryService.cs`
- `src/DevX.Indexer/BuildConfig.cs`
- `src/DevX.Indexer/IDiagnosticParser.cs`
- `src/DevX.Indexer/DiagnosticParsers/MsBuildDiagnosticParser.cs`
- `src/DevX.Indexer/DiagnosticParsers/GenericDiagnosticParser.cs`
- `tests/DevX.Tests/Build/BuildSystemDiscoveryTests.cs`
- `tests/DevX.Tests/Build/DiagnosticParserTests.cs`

**Modified files:**
- `src/DevX.Indexer/DotnetBuildTestService.cs` → rename to `BuildTestService.cs`
- `src/DevX.Protocol/ProtocolEngine.cs` (inject `BuildTestService`)
- `src/DevX.Cli/Program.cs` (add `devx config`)
- `src/DevX.Cli/CapabilitiesComposer.cs` (add `config` command)
- `src/DevX.Domain/Entities.cs` (extend `ExecutionRecord` if needed)
- `src/DevX.Cli/DevXReport.cs` (generic operation names)

## 7. Verification Criteria

- [ ] `devx build` and `devx test` work on a .NET repo without any config file (backward compat)
- [ ] `devx build` and `devx test` work on a Node.js repo after `devx index` (auto-discovery)
- [ ] `.devx/config.json` is created on first `devx index` with the inferred settings
- [ ] Editing `.devx/config.json` overrides auto-discovery
- [ ] Custom shell script (`./build.sh`) works when configured manually
- [ ] Build diagnostics are parsed for at least 3 toolchains (dotnet, cargo, npm)
- [ ] All existing tests pass (0 regressions)
- [ ] New test coverage ≥ 80% for discovery and parsers
