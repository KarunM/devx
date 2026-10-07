# Prompt: Implement SPEC-010 Structured Patch Engine

Implement SPEC-010 completely.

## Working Rules

- Use DevX first for discovery and validation.
- Read the spec document directly.
- Use `devx read` commands for source context where possible.
- Direct source reads/edits are allowed when DevX cannot yet express the change.
- Keep direct reads/edits minimal and report why they were necessary.
- Do not use unsafe `UpdateFile` for source files.
- Do not claim completion until every new or changed patch command path has been executed successfully.

## Implementation Focus

Implement structured patch support for:

- `ReplaceMethod`
- `InsertMethod`
- `AddUsing`
- existing `CreateFile`
- existing `DeleteFile`

Keep `UpdateFile` blocked for `.cs`, `.csproj`, and `.sln`.

## Required Validation

Run these with a real session id:

```bash
./devx patch preview tests/patches/replace-method.patch.json --session <id> --format json
./devx patch validate tests/patches/replace-method.patch.json --session <id> --format json
./devx patch apply tests/patches/replace-method.patch.json --session <id> --format json

./devx patch preview tests/patches/insert-method.patch.json --session <id> --format json
./devx patch validate tests/patches/insert-method.patch.json --session <id> --format json

./devx patch preview tests/patches/add-using.patch.json --session <id> --format json
./devx patch validate tests/patches/add-using.patch.json --session <id> --format json

./devx patch validate tests/patches/updatefile-source.patch.json --session <id> --format json

./devx build --session <id> --format json
./devx test --session <id> --format json
./devx report --session <id> --format json
```

If any command fails, fix the implementation and rerun it.

## Final Report

Include:

- files modified
- files created
- fixture files added
- verification commands executed
- actual command outcomes
- direct reads/edits performed and why
- remaining limitations
