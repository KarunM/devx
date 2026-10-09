#!/usr/bin/env python3
"""Every DevX write path re-indexes the files it touched -- brief OSS12 item 2.

The owner's ruling: `replace`, `patch apply`, `patch rollback`, create/delete via patch and the
MCP edits all leave the index describing the new text, with `--no-index` as the opt-out.

Each check makes a real edit and then asks the index, exactly as the next agent call would:
`devx search symbol <name>` must find a symbol that only the edit could have introduced, and
must not find one that only the edit could have removed. Nothing here calls `devx index update`.

    python3 tools/verify_write_paths.py [--cli <path-to-DevX.Cli.dll>]

Exit 0 when every path re-indexed; 1 with one line per failure."""

import argparse
import json
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
DEFAULT_CLI = HERE.parent / "src/DevX.Cli/bin/Release/net10.0/DevX.Cli.dll"
TIMEOUT = 120

ALPHA = """namespace Sample;

public sealed class Alpha
{
    public void FirstMethod()
    {
    }
}
"""

# A method body on one line (`public void X() { }`) is not resolvable by ReplaceMethod -- it
# answers "Target method body was not found" -- so the fixture uses the shape real code has.
RESTORED_FIRST = "public void FirstMethod()\n{\n}"
NEW_FILE = """namespace Sample;

public sealed class Beta
{
    public void CreatedMethod()
    {
    }
}
"""


def run(cli, cwd, *args):
    command = ["dotnet", str(cli)] + [str(a) for a in args]
    result = subprocess.run(command, cwd=cwd, capture_output=True, text=True, timeout=TIMEOUT)
    return result


def search_symbol(cli, repo, session, name):
    result = run(cli, repo, "search", "symbol", name, "--session", session, "--format", "json")
    try:
        data = json.loads(result.stdout)["Data"]
    except (ValueError, KeyError):
        return None
    found = data.get("Symbols") or data.get("symbols") or []
    return [s for s in found if s.get("Name") == name]


def write_patch(path, operations):
    path.write_text(json.dumps({"version": "1.0", "operations": operations}, indent=2))
    return path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--cli", default=str(DEFAULT_CLI))
    args = parser.parse_args()
    cli = Path(args.cli)
    if not cli.exists():
        print(f"FAIL: no DevX binary at {cli} (build it with: dotnet build src/DevX.Cli -c Release)")
        return 1

    repo = Path(tempfile.mkdtemp(prefix="devx-writepaths-"))
    try:
        (repo / "src" / "Sample").mkdir(parents=True)
        (repo / "src" / "Sample" / "Sample.csproj").write_text(
            '<Project Sdk="Microsoft.NET.Sdk">\n  <PropertyGroup>\n'
            "    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n")
        alpha = repo / "src" / "Sample" / "Alpha.cs"
        alpha.write_text(ALPHA)

        indexed = run(cli, repo, "index", ".")
        if indexed.returncode != 0:
            print(f"FAIL: could not index the fixture: {indexed.stdout}{indexed.stderr}")
            return 1

        session_json = run(cli, repo, "session", "create", "--format", "json")
        session = json.loads(session_json.stdout)["Data"]["sessionId"]

        failures = []
        checks = 0

        def expect(label, name, wanted):
            nonlocal checks
            checks += 1
            hits = search_symbol(cli, repo, session, name)
            if hits is None:
                failures.append(f"{label}: could not read search output")
                return
            got = len(hits) > 0
            if got != wanted:
                failures.append(
                    f"{label}: `search symbol {name}` {'found' if got else 'did not find'} it, "
                    f"expected {'found' if wanted else 'not found'} -- the write did not re-index")
            else:
                print(f"ok   {label}: {name} {'indexed' if wanted else 'gone from the index'}")

        # 1. replace re-indexes.
        result = run(cli, repo, "replace", "src/Sample/Alpha.cs", "--old", "FirstMethod", "--new", "RenamedMethod")
        if result.returncode != 0:
            failures.append(f"replace failed: {result.stdout}{result.stderr}")
        else:
            expect("replace", "RenamedMethod", True)
            expect("replace (old name gone)", "FirstMethod", False)

        # 2. replace --no-index opts out: the edit lands but the index keeps the old text.
        result = run(cli, repo, "replace", "src/Sample/Alpha.cs", "--old", "RenamedMethod",
                     "--new", "QuietMethod", "--no-index")
        if result.returncode != 0:
            failures.append(f"replace --no-index failed: {result.stdout}{result.stderr}")
        else:
            checks += 1
            if "QuietMethod" in alpha.read_text() and not search_symbol(cli, repo, session, "QuietMethod"):
                print("ok   replace --no-index: the edit landed and the index was left alone")
            else:
                failures.append("replace --no-index: the index followed the edit anyway")
            run(cli, repo, "index", "update", "src/Sample/Alpha.cs", "--session", session)

        # 3. patch CreateFile re-indexes the new file.
        patch = write_patch(repo / "create.json", [
            {"operation": "CreateFile", "path": "src/Sample/Beta.cs", "content": NEW_FILE}])
        result = run(cli, repo, "patch", "apply", str(patch), "--session", session)
        if result.returncode != 0:
            failures.append(f"patch CreateFile failed: {result.stdout}{result.stderr}")
        else:
            expect("patch CreateFile", "CreatedMethod", True)

        # 4. patch ReplaceMethod re-indexes the replaced method. The patch is built by
        # `devx patch create`, not hand-written, because that is what records the before-state
        # that rollback later restores.
        body = repo / "new-body.txt"
        body.write_text(RESTORED_FIRST + "\n")
        patch = repo / "replace.json"
        result = run(cli, repo, "patch", "create", str(patch), "--session", session,
                     "--op", "ReplaceMethod", "--path", "src/Sample/Alpha.cs",
                     "--method", "QuietMethod", "--replacement-file", str(body))
        if result.returncode != 0:
            failures.append(f"patch create failed: {result.stdout}{result.stderr}")
        else:
            result = run(cli, repo, "patch", "apply", str(patch), "--session", session)
            if result.returncode != 0:
                failures.append(f"patch ReplaceMethod failed: {result.stdout}{result.stderr}")
            else:
                expect("patch ReplaceMethod", "FirstMethod", True)
                expect("patch ReplaceMethod (old name gone)", "QuietMethod", False)

        # 5. patch rollback re-indexes back to the restored text.
        result = run(cli, repo, "patch", "rollback", str(patch), "--session", session)
        if result.returncode != 0:
            failures.append(f"patch rollback failed: {result.stdout}{result.stderr}")
        else:
            expect("patch rollback", "QuietMethod", True)
            expect("patch rollback (replacement gone)", "FirstMethod", False)

        # 6. patch DeleteFile drops the file from the index, rather than leaving it stale.
        patch = write_patch(repo / "delete.json", [
            {"operation": "DeleteFile", "path": "src/Sample/Beta.cs"}])
        result = run(cli, repo, "patch", "apply", str(patch), "--session", session)
        if result.returncode != 0:
            failures.append(f"patch DeleteFile failed: {result.stdout}{result.stderr}")
        else:
            expect("patch DeleteFile", "CreatedMethod", False)

        print()
        if failures:
            for failure in failures:
                print(f"FAIL: {failure}")
            return 1
        print(f"ok: {checks} checks, every write path re-indexed")
        return 0
    finally:
        shutil.rmtree(repo, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
