# ECP Roadmap (Capability-Based)

## Goal

Make ECP the primary interface for reading and editing repository code
so agents rarely need direct file access.

## Principles

-   Only task documents may be read directly.
-   All source reads should go through ECP.
-   All edits should use ECP patches when supported.
-   If ECP cannot express a change, the agent may edit directly but must
    explicitly report the limitation.

------------------------------------------------------------------------

# Phase 1 --- Reading

## ECP-READ-001

Add: - `ecp read file` - `ecp read method` - `ecp read symbol`

Acceptance: - Agents stop using direct "Read Program.cs" for repository
files.

------------------------------------------------------------------------

# Phase 2 --- Structured Patching

## ECP-PATCH-001

Support: - CreateFile - DeleteFile - ReplaceMethod - InsertMethod -
ReplaceClass - UpdateMarkdown

Acceptance: - Most edits are representable as structured patches.

------------------------------------------------------------------------

# Phase 3 --- Multi-file Patches

## ECP-PATCH-002

Support: - dependency ordering - validation - preview - rollback

Acceptance: - One patch can modify many files safely.

------------------------------------------------------------------------

# Phase 4 --- Patch Sessions

## ECP-PATCH-003

Support: - change sets - partial rollback - patch history - conflict
detection

------------------------------------------------------------------------

# Agent Policy

Agents should follow:

1.  Read task document directly.
2.  Read repository source only via ECP.
3.  Preview patch.
4.  Validate patch.
5.  Apply patch.
6.  Build.
7.  Test.
8.  Report.

Direct source edits are allowed only when ECP cannot express the change,
and the limitation must be reported.
