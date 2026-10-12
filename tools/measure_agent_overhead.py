#!/usr/bin/env python3
"""Measure deterministic DevX response sizes using a temporary fixture repository."""
import argparse, json, pathlib, subprocess, tempfile

def main():
    p = argparse.ArgumentParser()
    p.add_argument("binary"); p.add_argument("--edit-mode", choices=("patch", "replace"), default="patch")
    p.add_argument("--agent-format", action="store_true"); p.add_argument("--no-session", action="store_true")
    p.add_argument("--json-out")
    a = p.parse_args(); results = []
    with tempfile.TemporaryDirectory() as td:
        root = pathlib.Path(td); (root/"pkg").mkdir(); (root/"docs").mkdir(); (root/"tests").mkdir()
        (root/"pkg/util.py").write_text('def normalize_name(name):\n    return name.strip().lower()\n\ndef one(): return 1\ndef two(): return 2\n')
        (root/"pkg/big.py").write_text('def build_report():\n' + ''.join(f'    value_{i} = {i}\n' for i in range(400)) + '    return value_399\n')
        (root/"Widget.cs").write_text('class Widget { public void Frobnicate() {} }\n')
        (root/"docs/GUIDE.md").write_text('# Guide\n\n## Usage\n\nUse the utility.\n')
        (root/"tests/check_util.py").write_text('from pkg.util import normalize_name\nassert normalize_name(" X ") == "x"\n')
        base = [a.binary]; session = None
        if not a.no_session:
            run = subprocess.run(base+["session","create","--format","json"], cwd=root, capture_output=True, timeout=120, text=True)
            try:
                payload = json.loads(run.stdout)
                session = payload.get("Data", {}).get("sessionId") or payload.get("sessionId")
            except Exception: pass
            results.append(record("session create", run))
        def execute(label, args):
            cmd = base+args
            if session and "--session" not in cmd: cmd += ["--session", session]
            if a.agent_format: cmd += ["--format", "agent"]
            results.append(record(label, subprocess.run(cmd, cwd=root, capture_output=True, timeout=120, text=True)))
        execute("index .", ["index", "."]); execute("search symbol normalize_name", ["search","symbol","normalize_name"])
        execute("read method normalize_name", ["read","method","normalize_name"]); execute("read method build_report", ["read","method","build_report"])
        execute("snapshot pkg/util.py", ["snapshot","pkg/util.py"]); execute("test tests/check_util.py", ["test","tests/check_util.py"])
        if a.edit_mode == "replace": execute("replace", ["replace","pkg/util.py","--old","return name.strip().lower()","--new","return name.strip().casefold()"])
        else:
            execute("patch create", ["patch","create","p.json","--op","ReplaceText","--path","pkg/util.py","--old-text","return name.strip().lower()","--replacement","return name.strip().casefold()"])
            execute("patch apply", ["patch","apply","p.json"])
    total = sum(r["bytes"] for r in results)
    print("| Command | Bytes | Tokens | Exit code | Stderr (first 100 chars) |\n|---|---:|---:|---:|---|")
    for r in results:
        stderr = r.get("stderr", "").replace("|", "\\|")
        print(f'| {r["command"]} | {r["bytes"]} | {r["tokens"]} | {r["exit_code"]} | {stderr} |')
    print(f"\nTotal: {total} bytes, {total//4} tokens")
    if a.json_out: pathlib.Path(a.json_out).write_text(json.dumps({"results":results,"total_bytes":total,"total_tokens":total//4}, indent=2)+"\n")
def record(label, run):
    row={"command":label,"bytes":len(run.stdout.encode()),"tokens":len(run.stdout.encode())//4,"exit_code":run.returncode}
    if run.returncode: row["stderr"] = run.stderr[:100]
    return row
if __name__ == "__main__": main()
