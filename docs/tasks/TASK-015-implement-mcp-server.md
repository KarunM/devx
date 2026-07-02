# TASK-015: Implement MCP Server

Status: Ready

## Objective

Create src/Ecp.McpServer exposing ECP via MCP.

## Steps

1. Create project.
2. Register MCP tools.
3. Forward requests to ProtocolEngine.
4. Return ProtocolResponse JSON.
5. Add integration tests.

## Tools

- ecp_session_create
- ecp_index
- ecp_search_symbol
- ecp_search_text
- ecp_patch_apply
- ecp_build
- ecp_test
- ecp_diagnostics

## Definition of Done

An MCP-compatible client can execute the ECP workflow through the server.
