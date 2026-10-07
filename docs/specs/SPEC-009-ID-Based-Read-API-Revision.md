# SPEC-009 Revision: Canonical ID-Based Read APIs

## Motivation

Search APIs already return stable symbol IDs. Read operations should
consume those IDs directly to eliminate ambiguity and avoid extra
searches.

## Canonical Commands

### Read a file

``` bash
devx read file <path> [--range start:end | --full]
```

### Read a method

``` bash
devx read method --id <method-id>
devx read method --query <method-name-or-qualified-name>
```

-   `--id` is the preferred, deterministic form.
-   `--query` is a convenience lookup and may return `AmbiguousMatch`.

### Read a type

``` bash
devx read type --id <type-id>
devx read type --query <type-name-or-qualified-name>
```

-   `--id` is the preferred form.
-   `--query` may return `AmbiguousMatch`.

## Recommended Agent Flow

1.  `devx search symbol ...`
2.  Select the desired symbol ID.
3.  `devx read method --id ...` or `devx read type --id ...`
4.  Use snapshots/method context for analysis.
5.  Generate edits with `devx patch`.
6.  Validate and apply the patch.

## API Contract

Search responses should continue returning:

-   Id
-   Kind
-   QualifiedName
-   FilePath
-   Container
-   RepositoryId (optional)

Read responses should accept IDs directly instead of re-performing name
resolution.

## Benefits

-   No ambiguity
-   One search followed by deterministic reads
-   Better agent performance
-   Fewer repeated repository lookups
-   Natural progression: Search → Read → Patch
