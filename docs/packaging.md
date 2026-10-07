# Packaging measurements

Measurements supplied by the release orchestrator: **osx-arm64, .NET 10, 2026-10-07**. Commands below reproduce the measured operations.

## .NET tool package

```sh
dotnet pack src/DevX.Cli -c Release
```

Packing succeeds and produces `DevX.Cli.1.5.0.nupkg` at 39.8 MB. Package ID: `DevX.Cli`; installed command: `devx`. The measured pack emitted a warning that the package has no readme. `src/DevX.Cli/DevX.Cli.csproj` currently lacks package readme metadata, so no packaging metadata was changed as part of this documentation-only measurement record.

## Self-contained single-file publish

```sh
dotnet publish src/DevX.Cli -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true
```

This succeeds with a 97.9 MB binary. Warm `devx --version` takes 0.03 s. Warm `mcp serve` initialization takes 0.08–0.09 s; the first cold run takes 0.38 s. Framework-dependent `--version` baseline is 0.03 s.

## Native AOT

```sh
dotnet publish src/DevX.Cli -c Release -r osx-arm64 --self-contained -p:PublishAot=true
```

This produces a 20.4 MB binary and `--version` runs in about 0.01 s. Real commands such as `mcp serve` and `search` crash at runtime with “Reflection-based serialization has been disabled.” The release measurement identified reflection-based System.Text.Json use in `SqliteSessionStore.cs:231` and other session-store code, along with IL2026/IL3050 warnings in `ProtocolEngine.cs` and the session store. Microsoft.CodeAnalysis (Roslyn) is also not trim/AOT friendly. The blockers are source-generated System.Text.Json metadata across the codebase and Roslyn compatibility, requiring a larger refactor.

## Recommendation and release state

Ship as a `dotnet tool` now. Treat self-contained single-file publishing as an optional later distribution format. Native AOT is not feasible without the larger serialization and Roslyn work. There is no release CI yet.
