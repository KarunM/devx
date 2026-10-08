# MCP server

DevX provides a local Model Context Protocol server over stdio. It lets an MCP client use the current working directory as its repository, with a local SQLite session created when the server starts. The server is local only: it has no authentication or network listener.

Start it from the repository you want to work with:

```sh
devx mcp serve
```

The transport uses newline-delimited JSON-RPC 2.0 on stdin and stdout; diagnostics belong on stderr. The repository session is created for each server process. The server does not index automatically at startup. If an index-dependent tool is called before the repository has indexed files, it returns `isError: true` with this text: “repository not indexed: call the `index` tool first (omit `files` to index the whole repository)”. Call `index` first to build the index.

## Tools

## Editing

Use `devx replace` or MCP `edit.replace` for simple text edits. CLI replacement verification is off by default; pass `--verify` to enable it (`--no-verify` explicitly skips it). MCP `edit.replace` verifies edits by default; pass `verify:false` to skip it. Verification builds the nearest C# project or compiles a Python file, then runs relevant tests (or the default test command when no relevant tests are found). A build failure keeps the edit in place; the agent decides whether to fix forward or revert it. Use the patch tools for structured or multi-file edits.

The server exposes `index` (incrementally update paths or, when `files` is absent/empty, index the whole repository using the same file selection and `.gitignore` handling as `devx index .`), `search` (symbol or text), `read` (file, method, type, or section), `snapshot`, `callers`, `callees`, `references`, `patch.preview`, `patch.validate`, `patch.apply`, `build`, `test`, and `report`. `index` returns the number of indexed files. The process creates a repository session at startup and applies it to tool calls automatically. Patch tools accept structured patch documents. Tool results are returned as MCP text content containing the DevX protocol response; failed operations set `isError`.

Canonical argument names are shown in `tools/list`. For one release, old names remain accepted as deprecated aliases:

| Tool(s) | Canonical | Deprecated alias |
| --- | --- | --- |
| `search` | `query` | — |
| `read`, `snapshot`, `callers`, `callees`, `references` | `target` | `query` |
| `index` | `files` (optional) | — |

`kind` remains the argument name for `search` and `read`.

## Client setup

Claude Code:

```sh
claude mcp add devx -- devx mcp serve
```

Cursor, in `.cursor/mcp.json`:

```json
{
  "mcpServers": {
    "devx": { "command": "devx", "args": ["mcp", "serve"] }
  }
}
```

Codex, in `~/.codex/config.toml`:

```toml
[mcp_servers.devx]
command = "devx"
args = ["mcp", "serve"]
```

## Limits and troubleshooting

This release supports stdio only and serves the current local working directory. It does not provide a remote endpoint or authentication. If the client cannot start DevX, check that `devx` is on the client process PATH and that the .NET tool is installed. If search returns no useful matches, run `devx index .` in the target repository and restart the client session.
