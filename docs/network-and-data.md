# What DevX sends over the network

By default, DevX makes no outbound network requests. Local indexing, search, source reads, session storage, and reports operate on the machine. The following network activity is initiated by explicit commands or configuration.

## Verified outbound-capable code

| Code location | Destination | User initiated? | Data leaves the machine? |
|---|---|---|---|
| `src/DevX.Cli/LlmClient.cs:253` (`LlmServerManager.ProbeAsync`) | The configured LLM endpoint plus its readiness path (`/v1/models` or `/api/tags`, depending on provider). | Yes. Invoked as part of `devx ask` when checking/starting the configured local model server. | The readiness GET sends the HTTP request and normal connection metadata; it does not include the prompt. |
| `src/DevX.Cli/LlmClient.cs:434` (`OllamaClient.CompleteAsync`) | Configured endpoint, `api/generate` (commonly a local Ollama server). | Yes. `devx ask` with Ollama configured. | Yes. The prompt, which can include repository context and tool results, is sent to that endpoint. |
| `src/DevX.Cli/LlmClient.cs:513` (`OpenAiClient.CompleteWithMetadataAsync`) | Configured OpenAI-compatible endpoint, `v1/chat/completions` (including OpenAI or a local compatible server). | Yes. `devx ask` with that provider configured. | Yes. Conversation prompt/context and tool results are sent. A configured API credential is sent as authentication only if the configured client implementation supplies it; this code path does not read or attach an API-key setting. |
| `src/DevX.Indexer/DotnetBuildTestService.cs:156` | Destination depends on the selected command/runtime. Build and test commands may contact NuGet feeds during restore. | Yes. User invokes DevX build/test functionality. | Potentially. Restore may send package requests and machine/package metadata; build/test source is not deliberately uploaded by DevX. |
| `src/DevX.Cli/Program.cs:1603` | Whatever executable the user selects with `devx run`; its destination depends on that program and its arguments. | Yes. Directly user-selected. | Potentially. The launched tool controls any network traffic and data it sends. |

`git status` at `src/DevX.Indexer/RepositoryIndexingService.cs:524` and the Python parser at `src/DevX.Domain/PythonSymbolReader.cs:28` are local process launches. The former invokes only `git status --porcelain`; the latter invokes the bundled parser. Neither is a network call. `LlmServerManager` can also launch a configured local server (`src/DevX.Cli/LlmClient.cs:210`); that process may have its own network behavior, outside DevX's HTTP requests.

## Configuration and data

`devx ask` uses the configured provider, endpoint, and model. It sends the prompt and context needed for the requested operation to that endpoint. For a hosted endpoint, that data leaves the machine and is subject to that provider's handling. For a local endpoint, it is sent over the configured local connection. No prompt is sent by DevX unless `devx ask` is run.

This review found no API-key loading or Authorization-header attachment in the current `LlmClient` implementation. Error response bodies and endpoint query/user-info are now omitted from surfaced failures/diagnostics. Build/test may invoke package restore; whether it uses a remote feed depends on the user's NuGet configuration and cache. Arbitrary programs launched through `devx run` and external LLM server processes are outside DevX's control.
