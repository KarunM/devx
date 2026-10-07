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

- devx_session_create
- devx_index
- devx_search_symbol
- devx_search_text
- devx_patch_apply
- devx_build
- devx_test
- devx_diagnostics

## Principles

- No duplicated protocol logic
- JSON request/response
- ProtocolEngine is authoritative
- Stateless transport
