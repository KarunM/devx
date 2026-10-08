#!/usr/bin/env python3
"""Smoke test a published single-file DevX binary against CLI and stdio MCP flows."""

import argparse
import json
import os
import queue
import shutil
import subprocess
import sys
import tempfile
import threading
from pathlib import Path

COMMAND_TIMEOUT = 120
MCP_TIMEOUT = 60


def run(binary, args, cwd=None):
    return subprocess.run(
        [str(binary), *args], cwd=cwd, text=True, stdout=subprocess.PIPE,
        stderr=subprocess.PIPE, timeout=COMMAND_TIMEOUT, check=False,
    )


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def smoke(binary):
    result = run(binary, ["--version"])
    require(result.returncode == 0, f"--version exited {result.returncode}")
    require(result.stdout.strip(), "--version did not print a version")
    print("PASS version", flush=True)

    with tempfile.TemporaryDirectory(prefix="devx-smoke-") as first, tempfile.TemporaryDirectory(prefix="devx-smoke-mcp-") as second:
        fixture = Path(first)
        (fixture / "sub").mkdir()
        (fixture / "a.py").write_text('def greet_user(name):\n    return "hi " + name\n', encoding="utf-8")
        (fixture / "sub" / "b.py").write_text("class Helper:\n    def run(self):\n        return 1\n", encoding="utf-8")
        (fixture / "Widget.cs").write_text("public class Widget { public int Frobnicate() => 1; }\n", encoding="utf-8")
        (fixture / "README.md").write_text("# Title\n\n## Usage\n\nRun it.\n", encoding="utf-8")
        session = run(binary, ["session", "create", "--format", "json"], fixture)
        require(session.returncode == 0, f"session create exited {session.returncode}")
        try:
            session_id = json.loads(session.stdout)["Data"]["sessionId"]
        except (ValueError, KeyError, TypeError) as exc:
            raise RuntimeError(f"could not parse session id: {exc}") from exc
        require(isinstance(session_id, str) and session_id, "session id was empty")
        print("PASS session", flush=True)

        indexed = run(binary, ["index", ".", "--session", session_id], fixture)
        require(indexed.returncode == 0, f"index exited {indexed.returncode}")
        import re
        counts = [int(value) for value in re.findall(r"indexed\s+(\d+)\s+files", indexed.stdout + indexed.stderr, re.I)]
        require(counts and max(counts) >= 3, "index output did not report at least 3 indexed files")
        print("PASS index", flush=True)

        greet = run(binary, ["search", "symbol", "greet_user", "--session", session_id], fixture)
        require(greet.returncode == 0 and "greet_user" in greet.stdout + greet.stderr, "symbol search did not find greet_user")
        print("PASS python symbol search", flush=True)
        frob = run(binary, ["search", "symbol", "Frobnicate", "--session", session_id], fixture)
        require(frob.returncode == 0 and "Frobnicate" in frob.stdout + frob.stderr, "symbol search did not find Frobnicate")
        print("PASS C# symbol search", flush=True)
        method = run(binary, ["read", "method", "greet_user", "--session", session_id], fixture)
        require(method.returncode == 0 and 'return "hi "' in method.stdout + method.stderr, "read method did not contain the expected return fragment")
        print("PASS read method", flush=True)
        section = run(binary, ["read", "section", "README.md#Usage", "--session", session_id], fixture)
        require(section.returncode == 0, "read section failed")
        print("PASS read section", flush=True)

        mcp_fixture = Path(second)
        shutil.copy2(fixture / "a.py", mcp_fixture / "a.py")
        shutil.copytree(fixture / "sub", mcp_fixture / "sub")
        shutil.copy2(fixture / "Widget.cs", mcp_fixture / "Widget.cs")
        shutil.copy2(fixture / "README.md", mcp_fixture / "README.md")
        process = subprocess.Popen([str(binary), "mcp", "serve"], cwd=mcp_fixture, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, bufsize=1)
        lines = queue.Queue()
        def read_lines():
            for line in process.stdout:
                lines.put(line)
            lines.put(None)
        threading.Thread(target=read_lines, daemon=True).start()
        next_id = 1
        def rpc(method_name, params=None):
            nonlocal next_id
            request_id = next_id
            next_id += 1
            message = {"jsonrpc": "2.0", "id": request_id, "method": method_name}
            if params is not None:
                message["params"] = params
            process.stdin.write(json.dumps(message) + "\n")
            process.stdin.flush()
            while True:
                line = lines.get(timeout=MCP_TIMEOUT)
                require(line is not None, f"MCP server exited during {method_name}")
                try:
                    response = json.loads(line)
                except ValueError:
                    continue
                if response.get("id") == request_id:
                    return response
        def notify(method_name, params=None):
            message = {"jsonrpc": "2.0", "method": method_name}
            if params is not None:
                message["params"] = params
            process.stdin.write(json.dumps(message) + "\n")
            process.stdin.flush()
        try:
            init = rpc("initialize", {"protocolVersion": "2025-03-26", "capabilities": {}, "clientInfo": {"name": "smoke", "version": "1"}})
            require("error" not in init, "MCP initialize returned an error")
            notify("notifications/initialized")
            listed = rpc("tools/list", {})
            tool_names = [tool.get("name") for tool in listed.get("result", {}).get("tools", [])]
            require("index" in tool_names and "search" in tool_names, "MCP tools/list omitted index or search")
            print("PASS MCP initialize and tools/list", flush=True)
            before = rpc("tools/call", {"name": "search", "arguments": {"kind": "symbol", "query": "greet_user"}})
            before_text = json.dumps(before.get("result", {}), ensure_ascii=False)
            require(before.get("result", {}).get("isError") is True and "not indexed" in before_text.lower(), "MCP search before indexing did not report not indexed")
            print("PASS MCP pre-index search", flush=True)
            indexed_call = rpc("tools/call", {"name": "index", "arguments": {}})
            index_result = indexed_call.get("result", {})
            content = index_result.get("content", [])
            index_text = content[0].get("text", "") if content else ""
            try:
                index_payload = json.loads(index_text)
            except (TypeError, ValueError):
                index_payload = index_text
            files_indexed = index_payload.get("filesIndexed") if isinstance(index_payload, dict) else None
            if files_indexed is None:
                # 1.6.8 answers the agent with compact text (JSON only for format: json), so the
                # count arrives as `filesIndexed: 3` rather than `"filesIndexed": 3`.
                match = re.search(r'"?filesIndexed"?\s*[:=]\s*(\d+)', index_text)
                files_indexed = int(match.group(1)) if match else None
            require("error" not in indexed_call and isinstance(files_indexed, int) and files_indexed >= 3, "MCP index did not report filesIndexed >= 3")
            print("PASS MCP index", flush=True)
            after = rpc("tools/call", {"name": "search", "arguments": {"kind": "symbol", "query": "greet_user"}})
            after_text = json.dumps(after.get("result", {}), ensure_ascii=False)
            require("greet_user" in after_text, "MCP search after indexing did not find greet_user")
            print("PASS MCP post-index search", flush=True)
        finally:
            process.stdin.close()
            try:
                process.wait(timeout=MCP_TIMEOUT)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()
                raise RuntimeError("MCP server did not exit after stdin closed")
        print("PASS MCP shutdown", flush=True)


def main():
    parser = argparse.ArgumentParser(description="Smoke test a published single-file DevX binary.")
    parser.add_argument("publish_dir", help="directory containing DevX.Cli (or DevX.Cli.exe) and PythonAst")
    args = parser.parse_args()
    publish_dir = Path(args.publish_dir)
    binary_name = "DevX.Cli.exe" if os.name == "nt" else "DevX.Cli"
    binary = publish_dir / binary_name
    try:
        require(binary.is_file(), f"published binary not found: {binary_name}")
        smoke(binary.resolve())
    except Exception as exc:
        print(f"FAIL smoke: {exc}", file=sys.stderr, flush=True)
        return 1
    print("SMOKE OK", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
