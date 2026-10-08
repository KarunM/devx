#!/usr/bin/env python3
"""End-to-end MCP harness (brief OSS10 item 6): drive the real MCP server over stdio.

    python3 tools/mcp_e2e.py --bin <DevX.Cli> --repo <worktree> --label before --verbose
    python3 tools/mcp_e2e.py --bin <DevX.Cli> --repo <worktree> --label after --json out.json

It runs the journey the LG1 transcript shows an agent attempting:

    search -> read EVERY name search returned -> line-range read -> section read
    -> edit.replace -> revert -> test one target

and prints, per tool and per transport (MCP and CLI), how many calls succeeded and the
median bytes the agent had to read. The point of the table is the "before" column: each
row is a place the agent fell out of the tool and into the shell.

A step counts as a success only when the tool said so -- nothing here guesses. The worktree
is left as it was found: the replace step reverts itself.
"""
import argparse
import json
import statistics
import subprocess
import sys
import time
from pathlib import Path

STEP_TIMEOUT = 300


class McpClient:
    """Newline-delimited JSON-RPC over the server's stdio, as the transport defines it."""

    def __init__(self, binary, cwd):
        self.binary = binary
        self.proc = subprocess.Popen([binary, 'mcp', 'serve'], cwd=cwd, text=True,
                                     stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                     stderr=subprocess.DEVNULL, bufsize=1)
        self._id = 0

    def _send(self, method, params):
        self._id += 1
        message = {'jsonrpc': '2.0', 'id': self._id, 'method': method, 'params': params}
        self.proc.stdin.write(json.dumps(message) + '\n')
        self.proc.stdin.flush()
        line = self.proc.stdout.readline()
        if not line:
            raise RuntimeError('MCP server closed the stream')
        return json.loads(line)

    def initialize(self):
        return self._send('initialize', {'protocolVersion': '2025-06-18', 'capabilities': {}})

    def call(self, name, arguments):
        reply = self._send('tools/call', {'name': name, 'arguments': arguments})
        if 'error' in reply:
            text = json.dumps(reply['error'])
            return True, text, len(text)
        result = reply.get('result') or {}
        text = ''.join(part.get('text', '') for part in result.get('content') or [])
        return bool(result.get('isError')), text, len(text)

    def close(self):
        try:
            self.proc.stdin.close()
        except OSError:
            pass
        try:
            self.proc.wait(timeout=10)
        except subprocess.TimeoutExpired:
            self.proc.kill()


class Recorder:
    """Success, bytes and the first line of the failure, per (transport, tool)."""

    def __init__(self):
        self.rows = {}

    def add(self, transport, tool, outcome):
        failed, text, size = outcome
        entry = self.rows.setdefault((transport, tool),
                                     {'ok': 0, 'n': 0, 'bytes': [], 'skipped': 0, 'why': []})
        if size is None:
            entry['skipped'] += 1
            return
        entry['n'] += 1
        entry['bytes'].append(size)
        if failed:
            first = (text or '').strip().splitlines()
            entry['why'].append(first[0][:160] if first else '(no message)')
        else:
            entry['ok'] += 1

    def skip(self, transport, tool):
        self.add(transport, tool, (True, '', None))

    def table(self, verbose=False):
        lines = []
        for (transport, tool), entry in sorted(self.rows.items()):
            median = int(statistics.median(entry['bytes'])) if entry['bytes'] else 0
            outcome = 'n/a (skipped)' if entry['n'] == 0 else f'{entry["ok"]:>2}/{entry["n"]:<2} ok'
            lines.append(f'{transport:3} {tool:22} {outcome:<12} median {median:>6} B')
            if verbose:
                lines.extend(f'      {why}' for why in entry['why'][:2])
        return lines

    def totals(self):
        tried = sum(entry['n'] for entry in self.rows.values())
        failed = sum(entry['n'] - entry['ok'] for entry in self.rows.values())
        return tried, failed


def devx(binary, cwd, args, session):
    """One CLI call in agent format. Returns (failed, text, bytes)."""
    result = subprocess.run([binary, *args, '--session', session], cwd=cwd, text=True,
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                            timeout=STEP_TIMEOUT)
    text = result.stdout or ''
    return result.returncode != 0, text, len(text)


def new_session(binary, cwd):
    result = subprocess.run([binary, 'session', 'create', '--format', 'json'], cwd=cwd,
                            text=True, capture_output=True, timeout=STEP_TIMEOUT, check=True)
    parsed = json.loads(result.stdout)
    return parsed.get('Data', {}).get('sessionId') or parsed['sessionId']


def call(client, repo, transport, session, tool, mcp_args, cli_args):
    """The same call down both transports, so a row is a fair comparison."""
    if transport == 'mcp':
        return client.call(tool, {'sessionId': session, **mcp_args})
    return devx(client.binary, repo, cli_args, session)


def names_from_search(text):
    """Every symbol name a search result offers, in the agent format the server emits."""
    found = []
    for line in text.splitlines():
        parts = line.split()
        if len(parts) >= 3 and parts[1].count('.') >= 1 and not parts[1].startswith('-'):
            found.append(parts[1])
    return found


def first_location(text):
    """The first 'file:line' a search result carries."""
    for line in text.splitlines():
        for token in line.split():
            if ':' in token and token.rsplit(':', 1)[-1].split('-')[0].isdigit():
                path, _, span = token.partition(':')
                return path, int(span.split('-')[0])
    return None, None


def markdown_file(repo):
    for path in sorted(Path(repo).rglob('*.md')):
        if any(part.startswith('.') or part == 'node_modules' for part in path.parts):
            continue
        return str(path.relative_to(repo))
    return None


def indexed_heading(client, repo, document, session, transport):
    """The heading the TOOL says it indexed, not the first '#' line in the file.

    Reading the file directly would make a section read fail for a reason belonging to this
    script rather than to devx, and the table has to measure devx.
    """
    _failed, text, _size = call(client, repo, transport, session, 'snapshot',
                                {'target': document},
                                ['snapshot', document, '--format', 'agent'])
    for line in text.splitlines():
        stripped = line.strip()
        for prefix in ('section ', 'method ', 'type '):
            if stripped.startswith(prefix):
                return stripped[len(prefix):].split('(')[0].split(':')[0].strip()
    return None


def test_target(repo):
    """One test id that exists in each pinned repo."""
    return 'queries.tests.QueriesTests' if 'django' in repo.lower() else 'sympy.core.tests.test_expr'


def run_journey(repo, session, client, recorder, queries):
    for transport in ('mcp', 'cli'):
        for query in queries:
            outcome = call(client, repo, transport, session, 'search',
                           {'query': query, 'kind': 'symbol'},
                           ['search', 'symbol', query, '--format', 'agent'])
            recorder.add(transport, 'search', outcome)
            names = names_from_search(outcome[1])
            if not names:
                continue

            # 1. read EVERY name search returned. This is the round-trip the brief is about.
            for name in names[:8]:
                recorder.add(transport, 'read-roundtrip',
                             call(client, repo, transport, session, 'read',
                                  {'target': name, 'kind': 'method'},
                                  ['read', 'method', name, '--format', 'agent']))

            hit_file, hit_line = first_location(outcome[1])

            # 2. a line range in a file, in both spellings the brief asks to accept.
            if hit_file:
                for spelling in (f'{hit_line}-{hit_line + 20}', f'{hit_line}:{hit_line + 20}'):
                    recorder.add(transport, 'read-range',
                                 call(client, repo, transport, session, 'read',
                                      {'target': hit_file, 'kind': 'file', 'range': spelling},
                                      ['read', 'file', hit_file, '--range', spelling, '--format', 'agent']))

            # 3. a markdown section, which keeps the file#heading form.
            document = markdown_file(repo)
            heading = indexed_heading(client, repo, document, session, transport) if document else None
            if heading:
                target = f'{document}#{heading}'
                recorder.add(transport, 'read-section',
                             call(client, repo, transport, session, 'read',
                                  {'target': target, 'kind': 'section'},
                                  ['read', 'section', target, '--format', 'agent']))

            # 4. one edit and its revert, unverified so the harness measures the tool and
            #    not a build. The CLI has no replace command at all -- edits go through
            #    `devx patch create --op` -- which is a parity gap to record, not a failure.
            if hit_file and transport == 'cli':
                recorder.skip(transport, 'edit.replace')
            elif hit_file:
                marker = f'e2e-{int(time.time())}'
                outcome = call(client, repo, transport, session, 'edit.replace',
                               {'file': hit_file, 'old': '# ', 'new': '# ' + marker + ' ',
                                'verify': False},
                               ['replace', '--file', hit_file, '--old', '# ',
                                '--new', '# ' + marker + ' '])
                recorder.add(transport, 'edit.replace', outcome)
                if not outcome[0]:
                    call(client, repo, transport, session, 'edit.replace',
                         {'file': hit_file, 'old': '# ' + marker + ' ', 'new': '# ',
                          'verify': False},
                         ['replace', '--file', hit_file, '--old', '# ' + marker + ' ',
                          '--new', '# '])

            # 5. the one test target, which is where a repo with its own runner falls out.
            target = test_target(repo)
            recorder.add(transport, 'test',
                         call(client, repo, transport, session, 'test',
                              {'target': target, 'timeoutSeconds': 300},
                              ['test', target, '--timeout', '300', '--format', 'agent']))


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--bin', required=True, help='the devx binary to drive')
    parser.add_argument('--repo', required=True, help='the worktree to run in')
    parser.add_argument('--label', default='run', help='before | after, for the table heading')
    parser.add_argument('--query', action='append', default=None,
                        help='symbol query to search for (default: per-repo)')
    parser.add_argument('--json', default=None, help='also write the table here')
    parser.add_argument('--reuse-session', default=None, help='skip session create and index')
    parser.add_argument('--verbose', action='store_true', help='print each failure message')
    args = parser.parse_args(argv)

    repo = str(Path(args.repo).resolve())
    queries = args.query or (['count', 'get_count'] if 'django' in repo.lower() else ['evalf'])
    session = args.reuse_session or new_session(args.bin, repo)

    # Always index, including when a session was reused. Skipping this step used to leave the
    # CLI transport unable to resolve an id the MCP transport accepted, so --reuse-session
    # aborted the CLI arm after one call. From 1.6.9 an up-to-date checkout answers
    # "already current" in about a fifth of a second, so the step costs nothing to keep.
    started = time.monotonic()
    subprocess.run([args.bin, 'index', '.', '--session', session], cwd=repo, check=True,
                   stdout=subprocess.DEVNULL)
    index_seconds = time.monotonic() - started

    client = McpClient(args.bin, repo)
    recorder = Recorder()
    try:
        client.initialize()
        run_journey(repo, session, client, recorder, queries)
    finally:
        client.close()

    print(f'# devx MCP e2e -- {args.label}')
    print(f'repo     {repo}')
    print(f'binary   {args.bin}')
    print(f'session  {session}')
    if index_seconds is not None:
        print(f'index    {index_seconds:.1f}s (a fresh full index; not part of any step below)')
    for line in recorder.table(verbose=args.verbose):
        print(line)
    tried, broken = recorder.totals()
    print(f'steps    {tried} calls, {broken} failed')
    if args.json:
        Path(args.json).write_text(json.dumps(
            {'label': args.label, 'repo': repo, 'binary': args.bin,
             'index_seconds': index_seconds,
             'rows': {f'{t}/{k}': v for (t, k), v in recorder.rows.items()}}, indent=2),
            encoding='utf-8')
    return 1 if broken else 0


if __name__ == '__main__':
    sys.exit(main())
