# Repository Layout

```text
docs/
  adr/
  specs/
  plans/
  tasks/

src/
  Ecp.Cli/
  Ecp.Core/
  Ecp.Protocol/
  Ecp.Indexer/
  Ecp.Storage/
  Ecp.Patching/

tests/
  Ecp.Tests/
```

Each project has a single responsibility and communicates through well-defined interfaces.
