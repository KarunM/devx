Implement the assigned ECP task.

Task:
<Task-XXX>

Rules

- Use the globally installed `ecp` CLI.
- Do not use `dotnet run` or `./ecp`.
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
- Prefer ECP structured patches.
- If ECP cannot express the required edit, make the smallest direct source edit necessary.
- Do not invent workarounds to avoid direct edits.
- Add or update tests.
- Validate the new feature before full build/test when practical.
- Keep progress updates brief.
- Do not narrate every command.
- Continue until the task is complete.

Preferred workflow

```bash
ecp index status --format json
ecp session create --format json   # only if needed

ecp search ...
ecp read ...

ecp patch preview ...
ecp patch validate ...
ecp patch apply ...

ecp build --session <id> --format json
ecp test --session <id> --format json
ecp report --session <id> --format json
```

Use `dotnet build` / `dotnet test` only if ECP cannot perform the validation.

Before completion

- Execute every new CLI command introduced by this task.
- Execute modified commands that were affected.
- Do not report completion until those commands succeed.

Final report

- Files modified
- Files created
- Verification commands
- `ecp report --session <id> --format json`
- Remaining limitations