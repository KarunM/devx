# Contributing to DevX

Thanks for helping improve DevX. Keep changes focused and describe their user impact.

## Prerequisites and validation

Install the .NET 10 SDK. From the repository root:

```sh
dotnet restore DevX.sln
dotnet build DevX.sln
dotnet test tests/DevX.Tests/DevX.Tests.csproj
dotnet test tests/DevX.Tests/DevX.Tests.csproj --collect:"XPlat Code Coverage"
dotnet format DevX.sln
```

Run the relevant tests for your change and include the commands and outcomes in your pull request. Follow `.editorconfig`; use `dotnet format` to identify and fix formatting issues.

## Branches and pull requests

Create a focused branch from the current default branch. Open a pull request with a clear summary, rationale, and validation results. Link related issues, call out compatibility or migration impact, and respond to review feedback. Do not combine unrelated refactors with feature changes.

Use a concise commit subject in the imperative mood, such as `Add MCP stdio transport`. Keep each commit focused; a conventional prefix such as `feat:`, `fix:`, or `docs:` is welcome.

## Contributor agreement and source headers

Before merge, sign the [individual CLA](CLA.md) using the CLA bot comment: `I have read the CLA Document and I hereby sign the CLA`.

New source files should include an SPDX identifier such as `// SPDX-License-Identifier: Apache-2.0` where appropriate. Do not add or rewrite headers across existing files as part of unrelated work.

## Security and project documentation

Report vulnerabilities through a GitHub private security advisory for this repository. Do not disclose security issues publicly before maintainers have had a chance to respond.

See the [MCP setup guide](docs/mcp.md), [extension point](docs/extension-point.md), and the project's [architecture decision records](docs/adr/) for design and product boundaries. Please also follow the [Code of Conduct](CODE_OF_CONDUCT.md).
