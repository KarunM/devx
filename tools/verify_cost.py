#!/usr/bin/env python3
"""What verifying after an edit costs: wall-clock and response bytes for the four call shapes.

Owner concern (brief OSS14 adjustment, 2026-10-09): `--verify` must not add overhead to an edit
that does not want it. This measures the claim instead of asserting it:

    edit                 replace, no --verify            (the 1.6.13 shape)
    edit --verify        replace + build + relevant tests
    edit --verify=build  replace + build only
    verify               devx verify alone (build + tests, no edit)

Usage:
    python3 tools/verify_cost.py --binary <DevX.Cli> --repo <path> --file <relpath> \
        --label net --comment "//" [--pythonpath <path>] [--repeats 1]

The tool writes the marker line into the file itself and restores the original at the end, so it
never leaves the repository changed. One line per case; no file contents are printed.
"""

from __future__ import annotations

import argparse
import os
import subprocess
import sys
import time
from pathlib import Path

MARKER = "devx-verify-cost-marker"
EDITED = MARKER + "-edited"


def time_call(binary: str, args: list[str], cwd: Path, env: dict[str, str]) -> tuple[float, int, int, str]:
    started = time.perf_counter()
    proc = subprocess.run([binary, *args], cwd=cwd, env=env, capture_output=True, text=True)
    elapsed = time.perf_counter() - started
    text = (proc.stdout or "") + (proc.stderr or "")
    return elapsed, len(text.encode("utf-8")), proc.returncode, text.strip()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--binary", required=True, help="path to the DevX.Cli binary under test")
    parser.add_argument("--repo", required=True)
    parser.add_argument("--file", required=True, help="a file inside --repo to edit")
    parser.add_argument("--label", required=True, help="what the repo is, for the report line")
    parser.add_argument("--comment", default="//", help="the language's line comment")
    parser.add_argument("--pythonpath", default=None, help="added to PYTHONPATH for the child")
    parser.add_argument("--repeats", type=int, default=1)
    args = parser.parse_args()

    binary = str(Path(args.binary).resolve())
    repo = Path(args.repo).resolve()
    target = repo / args.file
    if not target.is_file():
        print(f"FAIL {args.label}: {args.file} is not a file in {repo}", file=sys.stderr)
        return 1

    env = dict(os.environ)
    if args.pythonpath:
        env["PYTHONPATH"] = str((repo / args.pythonpath).resolve())
    original = target.read_text()

    def reset() -> None:
        target.write_text(original + f"\n{args.comment} {MARKER}\n")

    cases: list[tuple[str, list[str], bool]] = [
        ("edit", ["replace", args.file, "--old", MARKER, "--new", EDITED], True),
        ("edit --verify", ["replace", args.file, "--old", MARKER, "--new", EDITED, "--verify"], True),
        ("edit --verify=build", ["replace", args.file, "--old", MARKER, "--new", EDITED, "--verify=build"], True),
        ("verify", ["verify"], False),
        ("verify build", ["verify", "build"], False),
    ]

    print(f"# {args.label}  {args.file}  binary {Path(binary).name}")
    print(f"{'case':<20}{'sec':>8}{'bytes':>10}  exit  first line")
    try:
        for name, command, needs_marker in cases:
            best_seconds = float("inf")
            best_bytes = 0
            best_exit = 0
            first = ""
            for _ in range(max(1, args.repeats)):
                if needs_marker:
                    reset()
                seconds, size, code, text = time_call(binary, command, repo, env)
                if seconds < best_seconds:
                    best_seconds, best_bytes, best_exit = seconds, size, code
                    first = text.splitlines()[0] if text else ""
            print(f"{name:<20}{best_seconds:>8.2f}{best_bytes:>10}  {best_exit:<5} {first[:90]}")
    finally:
        target.write_text(original)
    return 0


if __name__ == "__main__":
    sys.exit(main())
