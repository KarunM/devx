Implement the assigned DevX task.

Task:
<Task-XXX>

Rules

- Use the globally installed `devx` CLI.
- Do not use `dotnet run` or `./devx`.
- Check index status first.
- Do not re-index if already indexed.
- Reuse the current session whenever possible.
- Create a new session only if none exists.
- Minimize context retrieval.
- Discover code in this order:
  1. search
  2. read method --id
  3. read type --id
  4. read file (bounded range)
  5. snapshot only if still required
- Prefer DevX structured patches.
- If DevX cannot express the required edit, make the smallest direct source edit necessary.
- Do not invent workarounds to avoid direct edits.
- Add or update tests.
- Validate the new feature before full build/test when practical.
- Keep progress updates brief.
- Do not narrate every command.
- Continue until the task is complete.

Preferred workflow

```bash
devx index status --format json
devx session create --format json   # only if needed

devx search ...
devx read ...

devx patch preview ...
devx patch validate ...
devx patch apply ...

devx build --session <id> --format json
devx test --session <id> --format json
devx report --session <id> --format json
```

Use `dotnet build` / `dotnet test` only if DevX cannot perform the validation.

Before completion

- Execute every new CLI command introduced by this task.
- Execute modified commands that were affected.
- Do not report completion until those commands succeed.

Final report

- Files modified
- Files created
- Verification commands
- `devx report --session <id> --format json`
- Remaining limitations