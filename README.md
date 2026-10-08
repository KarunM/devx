# DevX

DevX is a local code-intelligence CLI for software engineers and coding agents. It indexes repositories, helps find and read relevant code, and supports build and test validation so work stays grounded in the current codebase.

## Install

Install the `DevX.Cli` .NET tool package (requires the .NET 10 SDK), or download a single-file binary for your platform from the GitHub Releases page. The binary requires no .NET installation; verify it with `SHA256SUMS.txt` included with each release.

```sh
dotnet tool install --global DevX.Cli
devx --help
```

Claude Code MCP setup:

```sh
claude mcp add devx -- devx mcp serve
```

See the [MCP setup guide](docs/mcp.md) for details.

**Privacy:** DevX makes no automatic network calls. Network activity occurs only when you run commands that initiate it, such as `devx ask`, build/test operations that restore packages, or `devx run` with a network-using program. See [network and data](docs/network-and-data.md).

## Quick start (60 seconds)

From a repository root, index code and search it (provide an existing DevX session ID):

```sh
devx index .
devx search text "TODO" --session <session-id>
```

Create a session with `devx session create --format json`, then pass its ID to session-scoped commands.

## Editing

Simple edit -> `devx replace`; structured/multi-file edits -> `devx patch`.

## MCP

See the [MCP setup guide](docs/mcp.md).

## Benchmarks

Benchmark results will be published here; the protocol and task sets are in docs/benchmarks/ (coming with the first published run).

## Licence and contributing

DevX is licensed under [Apache-2.0](LICENSE); see [NOTICE](NOTICE). Contributions follow [CONTRIBUTING.md](CONTRIBUTING.md) and require the [CLA](CLA.md). See the [Code of Conduct](CODE_OF_CONDUCT.md) and [Security policy](SECURITY.md).

## Organization report status

The organization report is a **preview, unverified** aggregation of caller-supplied data. It does not authenticate identity or provide authoritative organization reporting.
