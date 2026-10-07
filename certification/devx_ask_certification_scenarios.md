# DevX ASK Workflow Certification Scenarios

These scenarios validate the DevX ASK workflow with local models such as `mlx-community/Qwen3-8B-4bit`.

Run each scenario in a fresh disposable copy of the repository and record:

- model used
- exit status
- final `Completed:` line
- source blocks captured
- raw edit response
- whether patch applied
- whether build passed
- whether tests passed
- failure reason, if any

Do not manually fix failed runs during certification.

---

## Scenario 1 — Modify an Existing Method

```text
Modify the existing PrintHelp method in src.DevX.Cli/Program.cs.

Expected behavior:
- The help text should document the existing ask workflow flags.
- The ask usage line should mention --context-workflow and --debug-ask.
- The Global Options section should mention --debug-ask.
- Existing help text for other commands should remain unchanged.

Implementation requirements:
- Modify only PrintHelp.
- Do not add new commands.
- Do not create new methods.
- Preserve the existing multi-line raw string style.
- Do not leave duplicate or conflicting ask usage lines.

Return SEARCH/REPLACE blocks only.
Multiple blocks are allowed if needed.
SEARCH blocks must preserve exact indentation.
```

---

## Scenario 2 — Rename a Method and Update Callers

```text
Rename the helper method HasFlag to HasArgFlag in src.DevX.Cli/Program.cs.

Expected behavior:
- Behavior must remain exactly the same.
- All existing callers must compile after the rename.
- No references to HasFlag should remain.

Implementation requirements:
- Rename the method declaration:
  static bool HasFlag(string[] args, string name)
  to:
  static bool HasArgFlag(string[] args, string name)
- Update every call site in Program.cs.
- Do not change the method body logic.
- Do not change unrelated helper methods such as GetOption.

Return one SEARCH/REPLACE block for each affected location.
SEARCH blocks must be copied exactly from source, including indentation.
```

---

## Scenario 3 — Add a Capability Property

```text
Add a new boolean property named SupportsJsonOutput to each CLI command capability item.

Expected behavior:
- Capabilities output should expose whether a command supports --format json.
- Commands that already call WriteResponse or explicitly support json should set SupportsJsonOutput to true.
- Commands that only print text can set SupportsJsonOutput to false.
- Existing capability fields and tests should continue to work.

Implementation requirements:
- Find the command capability model used by CapabilitiesComposer.
- Add the SupportsJsonOutput property to that model.
- Update CapabilitiesComposer so each command initializes this property.
- Update CapabilitiesComposerTests to assert the property exists for at least:
  - capabilities = true
  - ask = true
  - session = true
- Do not remove or rename existing capability fields.
- Do not change command names or descriptions.

Return SEARCH/REPLACE blocks only.
Multiple files may be modified.
SEARCH blocks must preserve exact indentation.
```

---

## Scenario 4 — Add a Helper Method

```text
Add a helper method in src.DevX.Cli/Program.cs to centralize missing session-id validation.

Expected behavior:
- Existing commands should behave the same.
- If --session is missing, the command should still print:
  Missing --session.
  and return 1.

Implementation requirements:
- Add a helper method named TryGetRequiredSessionId.
- It should accept string[] args and an out string sessionId.
- It should call GetOption(args, "--session").
- It should print Missing --session. and return false when missing.
- Use the helper in ReferencesAsync, CallersAsync, and CalleesAsync.
- Do not refactor every command in the file; only those three methods.

Return SEARCH/REPLACE blocks for every changed location.
SEARCH blocks must preserve exact indentation.
```

---

## Scenario 5 — Modify Two Files

```text
Add a new CLI command named version and update capability tests.

Expected behavior:
- Running devx version should succeed.
- Text output should print a simple version string, for example: 1.0.
- JSON output should return an object with a version property.
- Capabilities should list version as a known command.

Implementation requirements:
- In Program.cs, add case "version" to the command dispatch.
- Add a VersionAsync handler method.
- Update PrintHelp to list devx version.
- Update CapabilitiesComposer and CapabilitiesComposerTests so version is included in known commands.
- Do not modify the existing capabilities command behavior.

Return SEARCH/REPLACE blocks for every affected file.
Multiple blocks are expected.
SEARCH blocks must preserve exact indentation.
```

---

## Scenario 6 — Remove Obsolete Code

```text
Remove the duplicate return statement in LoadLlmConfig in src.DevX.Cli/Program.cs.

Expected behavior:
- LoadLlmConfig should still return the same LlmConfig when config parsing succeeds.
- There should be only one identical return statement at the end of the try block.
- Behavior must remain unchanged.

Implementation requirements:
- Remove only the duplicate unreachable return.
- Do not change config parsing logic.
- Do not change ApplyEnvOverrides.
- Do not change CreateServerLaunchConfig.

Return SEARCH/REPLACE blocks only.
SEARCH blocks must preserve exact indentation.
```

---

## Scenario 7 — Update a Switch Statement

```text
Update the command dispatch switch in RunAsync so "status" is an alias for "index status".

Expected behavior:
- Running devx status should call IndexStatusAsync.
- Existing devx index status behavior must remain unchanged.
- Unknown command behavior must remain unchanged.

Implementation requirements:
- Add a new case "status" in the RunAsync switch.
- The new case should return await IndexStatusAsync(connectionString, args, format).
- Do not create a new StatusAsync method.
- Update PrintHelp to include devx status as an alias for index status.

Return SEARCH/REPLACE blocks only.
Multiple blocks are allowed.
SEARCH blocks must preserve exact indentation.
```

---

## Scenario 8 — Change a Config Signature

```text
Add request timeout support to LlmConfig.

Expected behavior:
- LlmConfig should support a RequestTimeoutSeconds value.
- .devx/config.json may optionally include "requestTimeoutSeconds".
- If missing, default to the current behavior or a safe default such as 120.
- Existing config files without this field must still work.

Implementation requirements:
- Find the LlmConfig record/type.
- Add RequestTimeoutSeconds to the record/type.
- Update LoadLlmConfig in Program.cs to parse requestTimeoutSeconds.
- Update fallback LlmConfig construction sites in Program.cs.
- Do not remove existing fields:
  provider, model, endpoint, temperature, maxTokens, contextWindow, maxToolResultLength, conversationWindow.
- Update tests if there are existing LlmConfig/config parsing tests.

Return SEARCH/REPLACE blocks for every affected location.
Multiple files may be required.
SEARCH blocks must preserve exact indentation.
```

---

## Scenario 9 — Add a Unit Test

```text
Add unit tests for SearchReplaceBlockParser.

Expected behavior:
- The parser should reject NO_CHANGES when the edit request is additive and the feature is not present.
- The parser should continue to accept valid one-block and two-block SEARCH/REPLACE responses.

Implementation requirements:
- Modify SearchReplaceBlockParserTests.cs.
- Add a new test for NO_CHANGES handling if the parser exposes that behavior.
- If NO_CHANGES validation is implemented outside SearchReplaceBlockParser, add the test to the correct existing test class instead.
- Do not remove existing parser tests.
- Keep test style consistent with the current Xunit tests.

Return SEARCH/REPLACE blocks only.
SEARCH blocks must preserve exact indentation.
```

---

## Scenario 10 — Reserved Regression Slot

This slot is reserved for a future certification scenario, such as a repeated health-command regression run or another multi-file workflow test.

```text
Reserved. Do not run until a concrete scenario prompt is added.
```


## Certification Status Definitions

- **PASS**: workflow completed the requested edit or correctly concluded no source change was required.
- **FAIL-SAFE**: workflow rejected an unsafe or invalid model patch before mutation.
- **FAIL**: workflow crashed, applied an invalid patch, or left the tree broken.
- **PENDING**: scenario has not yet been run.

Known validated outcomes so far:
- Scenario 1 is a safe no-op case because `PrintHelp` already contains `--context-workflow` and `--debug-ask`; the workflow correctly rejected an identical patch with `no_effect_edit`.
- Scenario 2 reached edit generation with relevant source blocks, but the model invented non-existent `HasFlag` call sites; the validator correctly rejected the patch before applying it.

---

## Certification Result Table

| Scenario | Status | Model | Exit | Context OK | Source Blocks | Edit Response | Patch Applied | Build | Test | Failure / Notes |
|---|---|---|---:|---|---|---|---|---|---|---|
| 1 — Modify PrintHelp | PASS | mlx-community/Qwen3-8B-4bit | 1 | yes | full `PrintHelp` body captured | identical SEARCH/REPLACE | no | n/a | n/a | `no_effect_edit`; expected safe no-op because requested flags already exist |
| 2 — Rename HasFlag | FAIL-SAFE | mlx-community/Qwen3-8B-4bit | 1 | yes | `ReadAsync`, `HasFlag`, `AskAsync` | invented non-existent `HasFlag` call sites | no | n/a | n/a | rejected by SEARCH validation: `search_not_found / invented code` |
| 3 — Add SupportsJsonOutput | FAIL-SAFE / CONTEXT-PARTIAL | mlx-community/Qwen3-8B-4bit | 1 | yes | test file captured; `CapabilitiesComposer.cs` only class header captured | edit invented `SupportsJsonOutput` assertions without production model context | no | n/a | n/a | `context_complete_reason=source_blocks_captured`; patch rejected safely; tree protected |
| 4 — Add TryGetRequiredSessionId | PENDING |  |  |  |  |  |  |  |  |  |
| 5 — Add version command + tests | PENDING |  |  |  |  |  |  |  |  |  |
| 6 — Remove duplicate return | PENDING |  |  |  |  |  |  |  |  |  |
| 7 — Add status alias | PENDING |  |  |  |  |  |  |  |  |  |
| 8 — Add request timeout config | PENDING |  |  |  |  |  |  |  |  |  |
| 9 — Add parser unit tests | PENDING |  |  |  |  |  |  |  |  |  |
| 10 — Reserved regression slot | PENDING |  |  |  |  |  |  |  |  | optional future scenario |
