# MCP server

DevX provides a local Model Context Protocol server over stdio. It lets an MCP client use the current working directory as its repository, with a local SQLite session created when the server starts. The server is local only: it has no authentication, network listener, or enterprise entitlement gate. Local MCP is part of the Free edition.

Start it from the repository you want to work with:

```sh
devx mcp serve
```

The transport uses newline-delimited JSON-RPC 2.0 on stdin and stdout; diagnostics belong on stderr. The repository session is created for each server process. Index the repository first with `devx index .` to enable indexed search.

## Tools

The server exposes `index` (incrementally update the index for file paths), `search` (symbol or text), `read` (file, method, type, or section), `snapshot`, `callers`, `callees`, `references`, `patch.preview`, `patch.validate`, `patch.apply`, `build`, `test`, and `report`. The process creates a repository session at startup and applies it to tool calls automatically. Patch tools accept structured patch documents. Tool results are returned as MCP text content containing the DevX protocol response; failed operations set `isError`.

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
