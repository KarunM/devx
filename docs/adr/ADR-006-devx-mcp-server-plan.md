# DevX MCP Server Plan

## Current Position

DevX already has most of the hard engineering pieces needed for an MCP server:

- Indexing
- Sessions
- Search
- Snapshot
- Read file / method / type
- References / callers / callees
- Patch create / preview / validate / apply / rollback
- Artifacts
- Build / test
- Diagnostics
- Report
- Capabilities

The missing part is mostly an MCP transport and tool-schema layer over the existing DevX command/service surface.

## Recommendation

Build DevX as an MCP server first, then add a lightweight workflow agent as an optional client.

Recommended order:

1. Freeze DevX Core Patch Baseline v0.
2. Build DevX MCP Server MVP.
3. Add read-only MCP tools first.
4. Add patch create / preview / validate.
5. Add patch apply behind explicit approval.
6. Add runtime/framework discovery.
7. Add lightweight workflow agent.
8. Integrate EverCode through MCP or the same DevX service layer.

## MCP Server Scope

### Read-only tools

- devx.index_workspace
- devx.session_create
- devx.snapshot_file
- devx.search_text
- devx.search_symbol
- devx.read_file
- devx.read_method
- devx.read_type
- devx.references
- devx.callers
- devx.callees
- devx.diagnostics
- devx.report
- devx.capabilities

### Mutation tools

- devx.artifact_create
- devx.artifact_delete
- devx.artifact_path
- devx.patch_create
- devx.patch_preview
- devx.patch_validate
- devx.patch_apply
- devx.patch_rollback

### Verification tools

- devx.build
- devx.test

## Approval Model

Read-only tools should be allowed automatically.

Safe mutation-preparation tools can be allowed automatically:

- artifact_create
- patch_create
- patch_preview
- patch_validate

Dangerous mutation tools should require approval:

- patch_apply
- patch_rollback

Build/test can be allowed automatically in trusted local workspaces, or approval-gated in restricted environments.

## Framework and Runtime Discovery

DevX should support multi-framework repositories by detecting project targets during indexing.

A project target should include:

- name
- path
- runtime
- framework
- project type
- package manager
- build command
- test command
- dev command, if available
- diagnostics if unavailable

## .NET / C# Support

Detection:

- .sln
- .csproj
- Directory.Build.props
- global.json

Target types:

- solution
- library
- console app
- web app
- test project

Build command:

- dotnet build <solution-or-project>

Test command:

- dotnet test <solution-or-project>

Discovery rules:

- Prefer .sln at repository root.
- If no .sln exists, discover .csproj files.
- Classify test projects by package references such as xunit, NUnit, MSTest, or project name ending in Tests.

## React / Vite / JavaScript / TypeScript Support

Detection:

- package.json
- vite.config.js / vite.config.ts
- tsconfig.json
- package-lock.json
- pnpm-lock.yaml
- pnpm-workspace.yaml
- yarn.lock
- bun.lock / bun.lockb
- turbo.json
- nx.json

Package manager detection:

- pnpm-lock.yaml or pnpm-workspace.yaml => pnpm
- yarn.lock => yarn
- package-lock.json => npm
- bun.lock or bun.lockb => bun
- otherwise npm

Framework detection:

- React: dependencies/devDependencies include react
- Vite: dependencies/devDependencies include vite or @vitejs/*
- Next.js: dependencies/devDependencies include next
- Vue: dependencies/devDependencies include vue
- Svelte: dependencies/devDependencies include svelte or @sveltejs/*
- Angular: dependencies/devDependencies include @angular/core

Target types:

- app
- package
- library
- test package

Build command:

- <pm> run build, if scripts.build exists

Test command:

- <pm> test, if scripts.test exists

Dev command:

- <pm> run dev, if scripts.dev exists

Important rule:

- Do not guess missing scripts. If build/test is missing, mark it unavailable with diagnostics.

## Python Support

Detection:

- pyproject.toml
- requirements.txt
- setup.py
- setup.cfg
- poetry.lock
- Pipfile
- uv.lock
- tox.ini
- pytest.ini
- manage.py

Package manager / environment detection:

- uv.lock or pyproject with uv conventions => uv
- poetry.lock or pyproject with poetry => poetry
- Pipfile => pipenv
- requirements.txt => pip
- conda environment.yml => conda

Framework detection:

- Django: manage.py or django dependency
- FastAPI: fastapi dependency
- Flask: flask dependency
- Pytest: pytest dependency or pytest.ini
- Celery: celery dependency

Build command:

- Python projects often have no build command.
- If pyproject has build-system, use python -m build only if build dependency exists or is configured.
- Otherwise mark build unavailable.

Test command:

- pytest, if pytest is detected
- python -m unittest, if unittest-style tests are detected and no pytest exists
- tox, if tox.ini exists

Dev command:

- Django: python manage.py runserver
- FastAPI: uvicorn <module>:app --reload only if an app module can be confidently detected; otherwise unavailable
- Flask: flask run only if Flask app config is detected; otherwise unavailable

Important rule:

- Avoid guessing app entrypoints. Return diagnostics instead.

## Monorepo Discovery

Detect workspace roots from:

- package.json workspaces
- pnpm-workspace.yaml
- turbo.json
- nx.json
- .sln
- pyproject workspace configuration, where applicable

For each target:

- assign stable target id
- record root path
- record framework/runtime
- record available scripts
- record build/test commands
- record dependency relationships if available

MCP should expose:

- devx.targets_list
- devx.target_info
- devx.build_target
- devx.test_target

CLI equivalents:

- devx diagnostics --session <id>
- devx build --session <id> [--project <name|path>]
- devx test --session <id> [--project <name|path>]

## Suggested MCP Tool Schemas

### devx.targets_list

Input:

- sessionId

Output:

- targets[]
  - id
  - name
  - path
  - runtime
  - framework
  - projectType
  - packageManager
  - buildAvailable
  - testAvailable

### devx.build

Input:

- sessionId
- targetId optional

Output:

- success
- command
- output
- diagnostics[]

### devx.test

Input:

- sessionId
- targetId optional

Output:

- success
- command
- output
- passed
- failed
- skipped
- diagnostics[]

## Implementation Plan

### Phase 1: Freeze DevX Core

- Tag current patch/artifact/build/test baseline.
- Record command contract.
- Record known caveats.

### Phase 2: Service Layer Extraction

Create reusable internal services so CLI and MCP share implementation:

- SessionService
- IndexService
- SearchReadService
- PatchService
- ArtifactService
- BuildTestService
- DiagnosticsService
- CapabilitiesService

CLI should call services.
MCP should call the same services.

### Phase 3: MCP MVP

Implement MCP transport:

- initialize
- tools/list
- tools/call

Expose read-only tools first.

### Phase 4: MCP Patch Tools

Expose:

- artifact_create
- patch_create
- patch_preview
- patch_validate
- patch_apply with approval requirement
- patch_rollback with approval requirement

### Phase 5: Runtime Discovery

Add target discovery for:

- .NET
- React/Vite/JS/TS
- Python

Expose through diagnostics and MCP target tools.

### Phase 6: Lightweight Workflow Agent

Build a small chat-driven workflow on top of DevX/MCP:

- understand request
- inspect repository
- create patch
- preview
- validate
- ask approval
- apply
- build/test
- summarize

## Challenges

### Security

- Restrict all operations to indexed workspace.
- Prevent path traversal.
- Separate read-only and mutating tools.
- Require approval for patch apply and rollback.

### Large Outputs

- Add truncation and pagination.
- Prefer source ranges and method reads over whole files.

### Tool Schema Drift

- Generate MCP schemas from the same command/capabilities model used by CLI help.

### Framework Guessing

- Do not invent commands.
- Only use build/test scripts that exist.
- Return diagnostics for missing or ambiguous commands.

### Rollback

- Keep before-state metadata for non-create/delete operations.
- Treat patch apply as a transaction.

## Product Positioning

DevX should be positioned as:

> A safe codebase operations server for AI coding agents.

Not as:

- a general autonomous developer
- a universal patch standard yet
- a replacement for existing coding models

DevX provides:

- context grounding
- deterministic patching
- validation
- rollback
- build/test feedback
- auditability

AI models provide:

- reasoning
- planning
- intent generation
- code proposal

The strongest architecture is:

AI Agent / EverCode / Copilot / Claude / Codex
  -> DevX MCP Server
  -> DevX Core Services
  -> Repository

## Freeze Recommendation

Freeze now as:

DevX Core Patch Baseline v0

Included:

- patch create
- artifact create/delete/path
- patch preview/validate/apply/rollback
- build/test
- capabilities/help

Excluded:

- runtime discovery
- MCP server
- lightweight workflow agent
- EverCode integration

Next branch:

DevX Runtime Discovery v0
