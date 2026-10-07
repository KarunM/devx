# DevX

DevX is a local code-intelligence CLI for software engineers and coding agents. It indexes repositories, helps find and read relevant code, and supports build and test validation so work stays grounded in the current codebase.

## Install

Install the `DevX.Cli` .NET tool package; its command is `devx`.

```sh
dotnet tool install --global DevX.Cli
devx --help
```

## Quick start (60 seconds)

From a repository root, index code and search it (provide an existing DevX session ID):

```sh
devx index .
devx search text "TODO" --session <session-id>
```

Create a session with `devx session create --format json`, then pass its ID to session-scoped commands.

## MCP

See the [MCP setup guide](docs/mcp.md).

## Free and Enterprise

Free includes unlimited local repositories, indexing, search, source reads, patches, validation and testing, local agents and shared sessions, local MCP, and local metrics and reporting. Core engineering work is available offline without DevX authentication. Enterprise adds organization accounts, OIDC/SSO, RBAC, shared remote indexing, audit trails, central policy, team analytics, integrations, and private deployment. Enterprise code is not in this repository; the entitlement seam is an architecture boundary, not a claim that all listed capabilities are implemented.

## Benchmark results (to be added)

Benchmark results will be published here when available.

## Licence and contributing

DevX is licensed under [Apache-2.0](LICENSE); see [NOTICE](NOTICE). Contributions follow [CONTRIBUTING.md](CONTRIBUTING.md) and require the [CLA](CLA.md). See the [Code of Conduct](CODE_OF_CONDUCT.md) and [Security policy](SECURITY.md).

## Organization report status

The organization report is a **preview, unverified** aggregation of caller-supplied data. It does not authenticate identity or provide authoritative organization reporting.
