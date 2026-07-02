# SPEC-008: MCP Adapter

Status: Draft

## Purpose
Expose the Engineering Context Protocol through the Model Context Protocol (MCP).

## Architecture

LLM Client
↓
MCP Server
↓
ProtocolEngine
↓
Repository Index / Session / Patch / Build

## Tool Mapping

- ecp_session_create
- ecp_index
- ecp_search_symbol
- ecp_search_text
- ecp_patch_apply
- ecp_build
- ecp_test
- ecp_diagnostics

## Principles

- No duplicated protocol logic
- JSON request/response
- ProtocolEngine is authoritative
- Stateless transport
