# DevX

DevX is a local code-intelligence CLI for software engineers and coding agents. It indexes repositories, helps find and read relevant code, and supports build and test validation so work stays grounded in the current codebase.

## Install

**Download the binary (no .NET required).** Pick the archive for your platform from the [Releases page](https://github.com/KarunM/devx/releases) (`osx-arm64`, `osx-x64`, `linux-x64`, `linux-arm64` as tar.gz; `win-x64` as zip). Verify it against `SHA256SUMS.txt`, extract it, and put `devx` on your PATH.

```sh
tar -xzf devx-<version>-<rid>.tar.gz
chmod +x devx
mv devx ~/.local/bin/
devx --help
```

On Windows, unzip the archive and add the folder to your PATH, then run `devx --help`.

**Or install as a .NET tool** (requires the .NET 10 SDK):

```sh
dotnet tool install --global DevX.Cli
devx --help
```

**Supported languages.** Symbol indexing covers Python and C# source, and Markdown headings are indexed as sections. Every other file type is covered by `devx search text` (ripgrep-backed, with coverage reporting) and `devx read file --range`. Python indexing needs `python3` on your PATH.

Claude Code MCP setup:

```sh
claude mcp add devx -- devx mcp serve
```

See the [MCP setup guide](docs/mcp.md) for details.

**Privacy:** DevX makes no automatic network calls. Network activity occurs only when you run commands that initiate it, such as `devx ask`, build/test operations that restore packages, or `devx run` with a network-using program. See [network and data](docs/network-and-data.md).

## Quick start (60 seconds)

From a repository root, index code and search it (provide an existing DevX session ID):

```sh
devx index .
devx search text "TODO" --session <session-id>
```

**Sessions are shared.** Create a session once per repository with `devx session create --format json`; its id is stored in the repository's `.devx/` database (`devx.db`). Any process or agent working in that repository can pass the same id, so one orchestrator can create the session and hand the id to several agents. The guidance block written by `devx init --agents <name>` tells an agent to use the id it was given and to create one only if none was given. Every session-scoped call records its command, duration and the `--agent <label>` that made it, so the history shows which agent read or searched what. Sessions outlive the shell that created them.

### Output format

When stdout is not a terminal -- a pipe, a redirect, an agent harness -- commands default to the lean **agent format**: one line per hit, capped at 10 with a `... N more` line, and a one-hit symbol search also returns that hit's source. The same default applies when a known agent marker is in the environment (`CODEX_*`, `CLAUDE*`, `CURSOR_*`, `AIDER_*`, `AGENT`, `AI_AGENT`), which covers harnesses that allocate a pty.

An explicit `--format` always wins: `--format json` for the full structured response, `--format text` for the human text, `--format agent` to force the lean form on a terminal.

## Editing

Use `devx replace` for simple text edits (`--verify` enables build/test verification; `--no-verify` disables it). Use `devx patch` for structured or multi-file edits. MCP `edit.replace` verifies edits by default and accepts `verify:false` to skip verification. A build failure keeps the edit in place; the agent decides whether to fix forward or revert it.

## MCP

See the [MCP setup guide](docs/mcp.md).

## Benchmarks

Benchmark results will be published here; the protocol and task sets are in docs/benchmarks/ (coming with the first published run).

## Licence and contributing

DevX is licensed under [Apache-2.0](LICENSE); see [NOTICE](NOTICE). Contributions follow [CONTRIBUTING.md](CONTRIBUTING.md) and require the [CLA](CLA.md). See the [Code of Conduct](CODE_OF_CONDUCT.md) and [Security policy](SECURITY.md).

## Organization report status

The organization report is a **preview, unverified** aggregation of caller-supplied data. It does not authenticate identity or provide authoritative organization reporting.
