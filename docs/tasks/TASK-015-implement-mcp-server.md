# TASK-015: Implement MCP Server

Status: Ready

## Objective

Create src/DevX.McpServer exposing DevX via MCP.

## Steps

1. Create project.
2. Register MCP tools.
3. Forward requests to ProtocolEngine.
4. Return ProtocolResponse JSON.
5. Add integration tests.

## Tools

- devx_session_create
- devx_index
- devx_search_symbol
- devx_search_text
- devx_patch_apply
- devx_build
- devx_test
- devx_diagnostics

## Definition of Done

An MCP-compatible client can execute the DevX workflow through the server.
