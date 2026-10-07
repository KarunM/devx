# DevX Roadmap (Capability-Based)

## Goal

Make DevX the primary interface for reading and editing repository code
so agents rarely need direct file access.

## Principles

-   Only task documents may be read directly.
-   All source reads should go through DevX.
-   All edits should use DevX patches when supported.
-   If DevX cannot express a change, the agent may edit directly but must
    explicitly report the limitation.

------------------------------------------------------------------------

# Phase 1 --- Reading

## DevX-READ-001

Add: - `devx read file` - `devx read method` - `devx read symbol`

Acceptance: - Agents stop using direct "Read Program.cs" for repository
files.

------------------------------------------------------------------------

# Phase 2 --- Structured Patching

## DevX-PATCH-001

Support: - CreateFile - DeleteFile - ReplaceMethod - InsertMethod -
ReplaceClass - UpdateMarkdown

Acceptance: - Most edits are representable as structured patches.

------------------------------------------------------------------------

# Phase 3 --- Multi-file Patches

## DevX-PATCH-002

Support: - dependency ordering - validation - preview - rollback

Acceptance: - One patch can modify many files safely.

------------------------------------------------------------------------

# Phase 4 --- Patch Sessions

## DevX-PATCH-003

Support: - change sets - partial rollback - patch history - conflict
detection

------------------------------------------------------------------------

# Agent Policy

Agents should follow:

1.  Read task document directly.
2.  Read repository source only via DevX.
3.  Preview patch.
4.  Validate patch.
5.  Apply patch.
6.  Build.
7.  Test.
8.  Report.

Direct source edits are allowed only when DevX cannot express the change,
and the limitation must be reported.
