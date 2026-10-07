# Repository Layout

```text
docs/
  adr/
  specs/
  plans/
  tasks/

src/
  DevX.Cli/
  DevX.Application/
  DevX.Domain/
  DevX.Protocol/
  DevX.Infrastructure/
    Storage/
    Indexing/
    Build/
    Patching/
    Parsers/
      CSharp/

tests/
  DevX.Tests/
```

Each project has a single responsibility and communicates through well-defined interfaces.
