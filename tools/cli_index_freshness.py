#!/usr/bin/env python3
"""Check that `devx index .` stops when the index is already current.

1.6.8 gave the MCP `index` tool a short-circuit and left the CLI command walking and
re-parsing every file. On the Django benchmark checkout (3,292 files) that is over 13
minutes to arrive at the index already on disk. This runs the CLI against a throwaway git
repository, so it is fast and does not depend on any benchmark checkout.

    python3 tools/cli_index_freshness.py <path-to-DevX.Cli>
"""

import re
import subprocess
import sys
import tempfile
import time
from pathlib import Path

TIMEOUT = 300


def devx(binary, cwd, *args):
    started = time.monotonic()
    result = subprocess.run(
        [str(binary), *args], cwd=cwd, text=True,
        stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=TIMEOUT, check=False,
    )
    return result.returncode, result.stdout or "", time.monotonic() - started


def git(cwd, *args):
    subprocess.run(["git", *args], cwd=cwd, check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def main():
    if len(sys.argv) != 2:
        print("usage: cli_index_freshness.py <path-to-DevX.Cli>", file=sys.stderr)
        return 2
    binary = Path(sys.argv[1]).resolve()
    if not binary.is_file():
        print(f"FAIL: binary not found: {binary}", file=sys.stderr)
        return 1

    with tempfile.TemporaryDirectory(prefix="devx-cli-fresh-") as root:
        Path(root, "pkg").mkdir()
        Path(root, "pkg", "mod.py").write_text("def greet_user(name):\n    return 'hi ' + name\n", encoding="utf-8")
        git(root, "init", "-q")
        git(root, "add", ".")
        git(root, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-qm", "init")

        code, first, first_seconds = devx(binary, root, "index", ".")
        if code != 0 or "Indexed" not in first:
            print(f"FAIL: first index exited {code}: {first.strip()}", file=sys.stderr)
            return 1
        print(f"PASS first index ({first_seconds:.1f}s): {first.strip()}")

        code, second, second_seconds = devx(binary, root, "index", ".")
        if code != 0 or "already current" not in second:
            print(f"FAIL: second index did not report already current: {second.strip()}", file=sys.stderr)
            return 1
        if second_seconds > 5:
            print(f"FAIL: already-current answer took {second_seconds:.1f}s", file=sys.stderr)
            return 1
        print(f"PASS already current ({second_seconds:.1f}s): {second.strip()}")

        # A file that has changed must still be indexed, or the short-circuit is a lie.
        Path(root, "pkg", "mod.py").write_text("def greet_user(name):\n    return 'hello ' + name\n", encoding="utf-8")
        code, third, _ = devx(binary, root, "index", ".")
        if code != 0 or "already current" in third or "Indexed" not in third:
            print(f"FAIL: a changed tree was not re-indexed: {third.strip()}", file=sys.stderr)
            return 1
        print(f"PASS changed tree re-indexed: {third.strip()}")

        code, forced, _ = devx(binary, root, "index", ".", "--force")
        if code != 0 or not re.search(r"Indexed\s+\d+\s+files", forced):
            print(f"FAIL: --force did not run a full index: {forced.strip()}", file=sys.stderr)
            return 1
        print(f"PASS --force: {forced.strip()}")

    print("CLI INDEX FRESHNESS OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
