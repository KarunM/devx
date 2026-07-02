Implement the assigned ECP task.

Task: 'Task-XXX'

Rules:
- Use ECP for repository discovery.
- Minimize context retrieval.
- Read only what is required.
- Reuse the current session.
- Add/update tests.
- Continue until the task is complete.
- Work silently
- Do not narrate
- Do not re-index if the repository is already indexed.
- Reuse the existing session unless explicitly instructed to create a new one.
- Do not expand methods or snapshots unless required for implementation.
- Do not report completion until every newly added CLI command has been executed successfully.
- Prefer validating the new feature before build/test when practical.

Do not edit repository files directly.

Generate changes as an ECP patch and apply them only through (in case of single file area patch):

```bash
./ecp patch preview
./ecp patch validate
./ecp patch apply
```

Direct IDE patch application is not permitted unless ECP patch support cannot express the required change.

Workflow:

```bash
./ecp index .
./ecp session create --format json
```

Use ECP for discovery and validation.

Prefer:

- search
- snapshot
- method summary
- method

Validate with:

```bash
./ecp build --session <id> --format json
./ecp test --session <id> --format json
./ecp report --session <id> --format json
```

Use `dotnet build` / `dotnet test` only if ECP cannot perform the validation.

At completion provide:

- Files modified
- Files created
- Verification commands
- `ecp report --session <id> --format json`
- Remaining limitations

After implementation:

1. Run build.
2. Run tests.
3. Execute every NEW CLI command introduced by this task.
4. Execute existing commands ONLY if they were modified or affected.
5. Do not report completion until every new command succeeds.

Keep progress updates brief.
Do not narrate every command.