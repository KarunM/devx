#!/usr/bin/env python3
"""Compare `--format json` output between two DevX binaries.

Part of the release gate: a release may add commands and change messages, but an explicit
`--format json` call must keep returning the same bytes, because agents and the runner parse
it. The two binaries are run against identical throwaway checkouts of the same fixture, so a
difference is a real difference in the CLI and not a difference in the repository.

Volatile values are normalised before comparing -- session ids, repository paths, timings and
the version string all change on every run and are not what this checks. A difference in
anything else is reported with the offending lines.

    python3 tools/json_output_regression.py <old-binary> <new-binary>
"""

import argparse
import json
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

TIMEOUT = 120

FIXTURE = {
    "a.py": 'def greet_user(name):\n    return "hi " + name\n',
    "sub/b.py": "class Helper:\n    def run(self):\n        return 1\n",
    "Widget.cs": "public class Widget { public int Frobnicate() => 1; }\n",
    "README.md": "# Title\n\n## Usage\n\nRun it.\n",
}

GUID = re.compile(r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", re.I)
VERSION = re.compile(r"\b\d+\.\d+\.\d+(?:[+-][0-9a-zA-Z.]+)?\b")


def run(binary, args, cwd):
    result = subprocess.run(
        [str(binary), *args], cwd=cwd, text=True,
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=TIMEOUT, check=False,
    )
    return result.returncode, result.stdout, result.stderr


def normalise(text, root, binary):
    """Replace the values that legitimately differ between two runs of two binaries."""
    text = GUID.sub("<guid>", text)
    text = text.replace(str(root), "<root>")
    # The checkout directory name is the project name and differs by design, because each
    # binary gets its own throwaway directory.
    text = text.replace(root.name, "<root-name>")
    text = text.replace(str(binary.resolve()), "<binary>")
    text = text.replace(str(binary.parent.resolve()), "<bindir>")
    text = VERSION.sub("<version>", text)
    # An ISO timestamp, and any bare integer long enough to be a duration in ms/us.
    text = re.sub(r"\d{4}-\d{2}-\d{2}T[\d:.]+Z?", "<timestamp>", text)
    text = re.sub(r'(: ?)(\d{4,})(\b)', r"\1<number>\3", text)
    return text


def workflow(binary, root):
    """Every JSON call a caller can make without an LLM endpoint configured."""
    steps = [
        ("capabilities", ["capabilities", "--format", "json"]),
        ("index status (before)", ["index", "status", "--format", "json"]),
        ("index .", ["index", ".", "--format", "json", "--session", "<session>"]),
        ("index status (after)", ["index", "status", "--format", "json"]),
        ("search symbol", ["search", "symbol", "greet_user", "--format", "json", "--session", "<session>"]),
        ("search text", ["search", "text", "greet", "--format", "json", "--session", "<session>"]),
        ("read method", ["read", "method", "greet_user", "--format", "json", "--session", "<session>"]),
        ("read section", ["read", "section", "README.md#Usage", "--format", "json", "--session", "<session>"]),
        ("snapshot", ["snapshot", "a.py", "--format", "json", "--session", "<session>"]),
    ]

    code, stdout, stderr = run(binary, ["session", "create", "--format", "json"], root)
    if code != 0:
        raise RuntimeError(f"session create exited {code}: {stderr.strip()[:200]}")
    try:
        session = json.loads(stdout)["Data"]["sessionId"]
    except (ValueError, KeyError) as error:
        raise RuntimeError(f"session create did not return a sessionId: {error}") from error

    outputs = {}
    for name, args in steps:
        code, stdout, stderr = run(binary, [a.replace("<session>", session) for a in args], root)
        outputs[name] = (code, normalise(stdout, root, binary), normalise(stderr, root, binary))
    return outputs


def checkout(binary, label):
    """A fresh copy of the fixture, plus the run against it. Returned so the caller can
    keep the directory alive while it reads the outputs."""
    root = Path(tempfile.mkdtemp(prefix=f"devx-json-{label}-"))
    for relative, content in FIXTURE.items():
        path = root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")
    return root, workflow(binary, root)


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("old_binary", help="the released binary to compare against")
    parser.add_argument("new_binary", help="the candidate binary")
    args = parser.parse_args()

    old_binary = Path(args.old_binary).resolve()
    new_binary = Path(args.new_binary).resolve()
    for binary in (old_binary, new_binary):
        if not binary.is_file():
            print(f"FAIL binary not found: {binary}")
            return 1

    roots = []
    differences = 0
    try:
        old_root, old_outputs = checkout(old_binary, "old")
        roots.append(old_root)
        new_root, new_outputs = checkout(new_binary, "new")
        roots.append(new_root)

        for name in old_outputs:
            old_code, old_out, old_err = old_outputs[name]
            new_code, new_out, new_err = new_outputs[name]
            if (old_code, old_out, old_err) == (new_code, new_out, new_err):
                print(f"PASS {name}")
                continue
            differences += 1
            print(f"DIFF {name}: exit {old_code} -> {new_code}")
            for label, before, after in (("stdout", old_out, new_out), ("stderr", old_err, new_err)):
                if before == after:
                    continue
                for index, (left, right) in enumerate(
                        zip(before.splitlines(), after.splitlines())):
                    if left != right:
                        print(f"  {label} line {index + 1}:")
                        print(f"    old: {left[:200]}")
                        print(f"    new: {right[:200]}")
                if len(before.splitlines()) != len(after.splitlines()):
                    print(f"  {label}: {len(before.splitlines())} lines -> {len(after.splitlines())}")
    finally:
        for root in roots:
            shutil.rmtree(root, ignore_errors=True)

    if differences:
        print(f"JSON OUTPUT REGRESSION: {differences} command(s) changed")
        return 1
    print("JSON OUTPUT UNCHANGED")
    return 0


if __name__ == "__main__":
    sys.exit(main())
