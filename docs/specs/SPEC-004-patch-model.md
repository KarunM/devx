# SPEC-004: Patch Model

- Status: Draft
- Version: 1.0

# Purpose

Defines the structured representation of source code modifications.

# Patch Operations

- CreateFile
- DeleteFile
- ReplaceType
- InsertMethod
- ReplaceMethod
- DeleteMethod
- RenameSymbol

# Validation

Every patch shall be validated before application.

# Workflow

Patch -> Apply -> Build -> Diagnostics -> Repair -> Repeat

# Principles

- Deterministic
- Idempotent where possible
- Explicit targets
- Machine readable
