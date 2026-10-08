#!/usr/bin/env python3
"""Compare raw runner output with DevX's compact build and test summaries."""

import subprocess
import sys
import tempfile
from pathlib import Path


def run(command, cwd, combine_stderr=True):
    result = subprocess.run(command, cwd=cwd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT if combine_stderr else subprocess.PIPE, text=True)
    return result.stdout


def tokens(output):
    return len(output.encode("utf-8")) / 4


def add_case(rows, name, raw, compact):
    raw_bytes = len(raw.encode("utf-8"))
    compact_bytes = len(compact.encode("utf-8"))
    reduction = (1 - compact_bytes / raw_bytes) * 100 if raw_bytes else 0
    rows.append((name, raw_bytes, tokens(raw), compact_bytes, tokens(compact), reduction))


def main():
    if len(sys.argv) != 2:
        raise SystemExit("usage: measure_build_test_output.py /path/to/devx")
    devx = str(Path(sys.argv[1]).resolve())
    rows = []
    with tempfile.TemporaryDirectory(prefix="devx-output-measure-") as temporary:
        root = Path(temporary)
        build = root / "build"
        build.mkdir()
        (build / "Sample.csproj").write_text("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>", encoding="utf-8")
        source = build / "Program.cs"
        source.write_text("using System;\nConsole.WriteLine(\"ok\");\n", encoding="utf-8")
        raw = run(["dotnet", "build", "--nologo"], build)
        compact = run([devx, "build"], build, combine_stderr=False)
        add_case(rows, "dotnet build pass", raw, compact)
        source.write_text("this is a deliberate compile error;\n", encoding="utf-8")
        raw = run(["dotnet", "build", "--nologo"], build)
        compact = run([devx, "build"], build, combine_stderr=False)
        add_case(rows, "dotnet build fail", raw, compact)

        pytest_available = subprocess.run(["python3", "-m", "pytest", "--version"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0
        if pytest_available:
            tests = root / "pytest"
            tests.mkdir()
            passing = tests / "test_pass.py"
            failing = tests / "test_fail.py"
            passing.write_text("def test_pass():\n    assert True\n", encoding="utf-8")
            failing.write_text("def test_fail():\n    assert 1 == 2\n", encoding="utf-8")
            raw = run(["python3", "-m", "pytest", "-q", str(passing)], tests)
            compact = run([devx, "test", str(passing)], tests, combine_stderr=False)
            add_case(rows, "pytest pass", raw, compact)
            raw = run(["python3", "-m", "pytest", "-q", str(failing)], tests)
            compact = run([devx, "test", str(failing)], tests, combine_stderr=False)
            add_case(rows, "pytest fail", raw, compact)

        scripts = root / "scripts"
        scripts.mkdir()
        script_cases = {
            "pass": ("print('start ' * 300)\nprint('57/57 checks passed')\n", 0),
            "fail": ("print('start ' * 300)\nprint('FAIL check_4: expected value')\nprint('56/57 checks passed')\n", 1),
            "crash": ("print('start ' * 300)\nprint('aborting')\nraise SystemExit(7)\n", 7),
        }
        for case, (body, _) in script_cases.items():
            script = scripts / f"{case}_check.py"
            script.write_text(body, encoding="utf-8")
            raw = run(["python3", str(script)], scripts)
            compact = run([devx, "test", str(script)], scripts, combine_stderr=False)
            add_case(rows, f"script {case}", raw, compact)

    print("| case | raw output bytes | raw tokens (len/4) | devx summary bytes | devx tokens | reduction % |")
    print("|---|---:|---:|---:|---:|---:|")
    for name, raw_bytes, raw_tokens, summary_bytes, summary_tokens, reduction in rows:
        print(f"| {name} | {raw_bytes} | {raw_tokens:.1f} | {summary_bytes} | {summary_tokens:.1f} | {reduction:.1f}% |")


if __name__ == "__main__":
    main()
