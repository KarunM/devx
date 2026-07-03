# ECP

ECP is a local code intelligence and engineering loop tool. On macOS, the simplest way to update an existing install is to rebuild the CLI tool from this repo and reinstall it.

## Install Or Update On Mac

From the repository root:

```bash
dotnet tool uninstall --tool-path ./.tools ecp 2>/dev/null || true
dotnet pack src/Ecp.Cli -c Release -o ./artifacts/tools
dotnet tool install --tool-path ./.tools --add-source ./artifacts/tools Ecp.Cli
./.tools/ecp --help
```

If you want to update a local repo install, rerun the pack and install commands above. If you previously installed the tool globally from a published NuGet feed, update it with:

```bash
dotnet tool update --global Ecp.Cli
```

That global update command will fail unless `Ecp.Cli` has been published to a NuGet feed available on your machine. For this repo today, the supported path is the local tool install shown above. If the installed tool still looks unchanged, uninstall it first and reinstall from the freshly packed local artifact.

```bash
dotnet tool uninstall --global ecp.cli
dotnet pack src/Ecp.Cli -c Release -o ./artifacts/tools
dotnet tool install --global --add-source ./artifacts/tools ecp.cli
```

If you want to run the CLI directly from the repo without installing a tool, use:

```bash
dotnet run --project src/Ecp.Cli -- --help
```

## Build And Test

```bash
dotnet build
dotnet test
```

## Contribute

Please keep changes focused and follow the existing solution structure under `src/` and `tests/`.


## Ask Session
SESSION_ID=$(ecp session create --format json | jq -r '.sessionId')
ecp ask "say hello and finish" --session "$SESSION_ID"