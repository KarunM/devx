# Repository Layout

```text
docs/
  adr/
  specs/
  plans/
  tasks/

src/
  Ecp.Cli/
  Ecp.Application/
  Ecp.Domain/
  Ecp.Protocol/
  Ecp.Infrastructure/
    Storage/
    Indexing/
    Build/
    Patching/
    Parsers/
      CSharp/

tests/
  Ecp.Tests/
```

Each project has a single responsibility and communicates through well-defined interfaces.
