# Dependency licences

Resolved package versions below come from each source project's and `tests/DevX.Tests` `obj/project.assets.json`; license expressions and legacy license URLs were read from the corresponding cached NuGet `.nuspec` under `~/.nuget/packages`. Each package is listed once, with all projects whose resolved assets include it. Build tooling and test-only packages are included.

`NOASSERTION` means the nuspec provides only a legacy URL or no license expression; it is not a claim that the package is incompatible. `LicenseRef-Public-Domain` records the bundled native SQLite code, which is public domain. URLs for SPDX expressions point to the NuGet license text.

| Package | Version | Dependency kind | SPDX license | License URL | Resolved in | Apache-2.0 compatible? |
|---|---:|---|---|---|---|---|
| coverlet.collector | 6.0.4 | Test-only | MIT | https://licenses.nuget.org/MIT | `tests/DevX.Tests` | Yes |
| Microsoft.CodeAnalysis.Analyzers | 3.3.4 | Transitive | MIT | https://licenses.nuget.org/MIT | `src/DevX.Cli`, `src/DevX.Indexer`, `src/DevX.Infrastructure`, `src/DevX.Protocol`, `tests/DevX.Tests` | Yes |
| Microsoft.CodeAnalysis.Common | 4.12.0 | Transitive | MIT | https://licenses.nuget.org/MIT | `src/DevX.Cli`, `src/DevX.Indexer`, `src/DevX.Infrastructure`, `src/DevX.Protocol`, `tests/DevX.Tests` | Yes |
| Microsoft.CodeAnalysis.CSharp | 4.12.0 | Direct in `src/DevX.Indexer`, `src/DevX.Infrastructure` | MIT | https://licenses.nuget.org/MIT | `src/DevX.Cli`, `src/DevX.Indexer`, `src/DevX.Infrastructure`, `src/DevX.Protocol`, `tests/DevX.Tests` | Yes |
| Microsoft.CodeCoverage | 17.12.0 | Transitive | MIT | https://licenses.nuget.org/MIT | `tests/DevX.Tests` | Yes |
| Microsoft.Data.Sqlite | 10.0.9 | Direct in `src/DevX.Infrastructure` | MIT | https://licenses.nuget.org/MIT | `src/DevX.Cli`, `src/DevX.Indexer`, `src/DevX.Infrastructure`, `src/DevX.Protocol`, `tests/DevX.Tests` | Yes |
| Microsoft.Data.Sqlite.Core | 10.0.9 | Transitive | MIT | https://licenses.nuget.org/MIT | `src/DevX.Cli`, `src/DevX.Indexer`, `src/DevX.Infrastructure`, `src/DevX.Protocol`, `tests/DevX.Tests` | Yes |
| Microsoft.DotNet.ILCompiler | 10.0.8 | Direct in `src/DevX.Application`, `src/DevX.Cli`, `src/DevX.Domain`, `src/DevX.Indexer`, `src/DevX.Infrastructure`, `src/DevX.Protocol` | MIT | https://licenses.nuget.org/MIT | `src/DevX.Application`, `src/DevX.Cli`, `src/DevX.Domain`, `src/DevX.Indexer`, `src/DevX.Infrastructure`, `src/DevX.Protocol` | Yes |
| Microsoft.NET.ILLink.Tasks | 10.0.8 | Direct in `src/DevX.Application`, `src/DevX.Cli`, `src/DevX.Domain`, `src/DevX.Indexer`, `src/DevX.Infrastructure`, `src/DevX.Protocol` | MIT | https://licenses.nuget.org/MIT | `src/DevX.Application`, `src/DevX.Cli`, `src/DevX.Domain`, `src/DevX.Indexer`, `src/DevX.Infrastructure`, `src/DevX.Protocol` | Yes |
| Microsoft.NET.Test.Sdk | 17.12.0 | Direct in `tests/DevX.Tests` | MIT | https://licenses.nuget.org/MIT | `tests/DevX.Tests` | Yes |
| Microsoft.TestPlatform.ObjectModel | 17.12.0 | Transitive | MIT | https://licenses.nuget.org/MIT | `tests/DevX.Tests` | Yes |
| Microsoft.TestPlatform.TestHost | 17.12.0 | Transitive | MIT | https://licenses.nuget.org/MIT | `tests/DevX.Tests` | Yes |
| Newtonsoft.Json | 13.0.1 | Transitive | MIT | https://licenses.nuget.org/MIT | `tests/DevX.Tests` | Yes |
| runtime.osx-arm64.Microsoft.DotNet.ILCompiler | 10.0.8 | Transitive | MIT | https://licenses.nuget.org/MIT | `src/DevX.Application`, `src/DevX.Cli`, `src/DevX.Domain`, `src/DevX.Indexer`, `src/DevX.Infrastructure`, `src/DevX.Protocol` | Yes |
| SQLitePCLRaw.bundle_e_sqlite3 | 2.1.11 | Transitive | Apache-2.0 | https://licenses.nuget.org/Apache-2.0 | `src/DevX.Cli`, `src/DevX.Indexer`, `src/DevX.Infrastructure`, `src/DevX.Protocol`, `tests/DevX.Tests` | Yes |
| SQLitePCLRaw.core | 2.1.11 | Transitive | Apache-2.0 | https://licenses.nuget.org/Apache-2.0 | `src/DevX.Cli`, `src/DevX.Indexer`, `src/DevX.Infrastructure`, `src/DevX.Protocol`, `tests/DevX.Tests` | Yes |
| SQLitePCLRaw.lib.e_sqlite3 | 3.50.3 | Direct in `src/DevX.Infrastructure` | LicenseRef-Public-Domain | https://www.sqlite.org/copyright.html | `src/DevX.Cli`, `src/DevX.Indexer`, `src/DevX.Infrastructure`, `src/DevX.Protocol`, `tests/DevX.Tests` | Yes |
| SQLitePCLRaw.provider.e_sqlite3 | 2.1.11 | Transitive | Apache-2.0 | https://licenses.nuget.org/Apache-2.0 | `src/DevX.Cli`, `src/DevX.Indexer`, `src/DevX.Infrastructure`, `src/DevX.Protocol`, `tests/DevX.Tests` | Yes |
| xunit | 2.9.2 | Direct in `tests/DevX.Tests` | Apache-2.0 | https://licenses.nuget.org/Apache-2.0 | `tests/DevX.Tests` | Yes |
| xunit.abstractions | 2.0.3 | Transitive | NOASSERTION | https://raw.githubusercontent.com/xunit/xunit/master/license.txt | `tests/DevX.Tests` | Unverified |
| xunit.analyzers | 1.16.0 | Transitive | Apache-2.0 | https://licenses.nuget.org/Apache-2.0 | `tests/DevX.Tests` | Yes |
| xunit.assert | 2.9.2 | Transitive | Apache-2.0 | https://licenses.nuget.org/Apache-2.0 | `tests/DevX.Tests` | Yes |
| xunit.core | 2.9.2 | Transitive | Apache-2.0 | https://licenses.nuget.org/Apache-2.0 | `tests/DevX.Tests` | Yes |
| xunit.extensibility.core | 2.9.2 | Transitive | Apache-2.0 | https://licenses.nuget.org/Apache-2.0 | `tests/DevX.Tests` | Yes |
| xunit.extensibility.execution | 2.9.2 | Transitive | Apache-2.0 | https://licenses.nuget.org/Apache-2.0 | `tests/DevX.Tests` | Yes |
| xunit.runner.visualstudio | 2.8.2 | Direct in `tests/DevX.Tests` | Apache-2.0 | https://licenses.nuget.org/Apache-2.0 | `tests/DevX.Tests` | Yes |

## Compatibility summary

The nuspec metadata identifies MIT and Apache-2.0 licenses for the packages with asserted SPDX expressions; both are Apache-2.0 compatible. The bundled native SQLite code is public domain. `xunit.abstractions` exposes only a legacy license URL in its nuspec, so its SPDX identifier and compatibility are marked `NOASSERTION`/unverified rather than inferred from outside the requested metadata source. No package with an asserted SPDX expression was identified as incompatible.
