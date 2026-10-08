"""Execute bundled request examples offline; synthetic review is a test fixture.

This verifies payloads and publication, not fresh-host semantic acceptance.
Only the public launcher is used; the helper imports no product internals.
"""

from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
from contextlib import contextmanager
from pathlib import Path


COMMAND_TIMEOUT_SECONDS = 30


@contextmanager
def trial_directory():
    root = Path(tempfile.mkdtemp(prefix="vw-workflow-examples-")).resolve()
    trial = {"path": root, "retain": False}
    try:
        yield trial
    except BaseException:
        # Preserve interrupted work without masking the command failure or
        # guessing whether a working DB needs committed-unpublished recovery.
        trial["retain"] = True
        raise
    finally:
        if trial["retain"]:
            print(f"Preserved workflow trial for diagnostics or recovery: {root}", file=sys.stderr)
        else:
            # Inspect the uniquely allocated trial before recursive cleanup.
            entries = list(root.iterdir())
            log_directories = [item for item in entries if item.name.startswith("vw-ndjson-")]
            for directory in log_directories:
                if (directory.is_symlink() or not directory.is_dir()
                        or directory.resolve().parent != root
                        or any(item.name != "responses.jsonl" or item.is_symlink()
                               or not item.is_file() or item.resolve().parent != directory.resolve()
                               for item in directory.iterdir())):
                    raise AssertionError(f"Refusing unexpected log cleanup: {directory}")
            if (root.parent != Path(tempfile.gettempdir()).resolve()
                    or not root.name.startswith("vw-workflow-examples-")
                    or any(item.name not in {"docs-vw.html", ".docs-vw.html.vw-lock", "responses.jsonl"}
                           or item.is_symlink() or not item.is_file()
                           or item.resolve().parent != root for item in entries if item not in log_directories)):
                raise AssertionError(f"Refusing unexpected trial cleanup: {root}")
            shutil.rmtree(root)


def verify(skill: Path, *, log_output: bool = False, deny_hard_links: bool = False) -> None:
    if deny_hard_links and os.name != 'nt':
        raise ValueError('Hard-link denial exercises the Windows publication contract.')
    reference_text = (skill / "references" / "command-workflow.md").read_text(encoding="utf-8")
    examples: dict[str, list[dict]] = {}
    for block in re.findall(r"```json\s*\n(.*?)\n```", reference_text, re.DOTALL):
        request = json.loads(block)
        examples.setdefault(request["command"], []).append(request)
    launcher = (skill / "scripts" / "validated_world.py").resolve()
    with trial_directory() as trial:
        document = trial["path"] / "docs-vw.html"
        response_log = trial["path"] / "responses.jsonl"
        bindings: dict = {"$PATH": str(document)}
        command = ([sys.executable, "-X", "utf8", "-I", "-S", str(skill / "scripts" / "ndjson_log.py")]
                   if log_output else [sys.executable, "-X", "utf8", "-I", "-S", str(launcher), "ndjson"])
        if deny_hard_links:
            # Test-only audit hook covers the actual isolated child, including
            # engine initialization and HTML publication, rather than a mock in
            # the parent process. Never changes permissions on the host.
            bootstrap = '''import runpy, sys
def deny(event, arguments):
    if event == "os.link":
        raise PermissionError("[WinError 5] fixture denies hard-link creation")
sys.addaudithook(deny)
sys.argv = sys.argv[1:]
runpy.run_path(sys.argv[0], run_name="__main__")
'''
            command = command[:5] + ['-c', bootstrap] + command[5:]
        process = subprocess.Popen(
            command,
            cwd=trial["path"],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, encoding="utf-8",
        )
        log_reader = None
        log_offset = 0

        def response_line(request_command):
            nonlocal log_reader, log_offset
            if not log_output:
                return process.stdout.readline()
            deadline = time.monotonic() + COMMAND_TIMEOUT_SECONDS
            pending = ""
            while time.monotonic() < deadline:
                if response_log.exists():
                    read = subprocess.run([sys.executable, '-I', '-S', str(skill / 'scripts/ndjson_log.py'),
                                           '--read', str(response_log), '--offset', str(log_offset)],
                                          capture_output=True, text=True, encoding='utf-8', timeout=10)
                    if read.returncode != 0: raise AssertionError(read.stderr)
                    payload = json.loads(read.stdout)
                    log_offset = payload['nextOffset']
                    if payload['responses']:
                        if len(payload['responses']) != 1: raise AssertionError('Unexpected extra responses')
                        return json.dumps(payload['responses'][0])
                if process.poll() is not None:
                    raise AssertionError(f"Log host exited without a complete response: {process.stderr.read()}")
                time.sleep(0.02)
            raise AssertionError(
                f"Live log response timed out during {request_command} after {COMMAND_TIMEOUT_SECONDS}s; "
                f"host status={process.poll()}, log={response_log}, incomplete characters={len(pending)}")

        def expand(value):
            if isinstance(value, str):
                return bindings.get(value, value)
            if isinstance(value, list):
                return [expand(item) for item in value]
            if isinstance(value, dict):
                return {key: expand(item) for key, item in value.items()}
            return value

        def send(command: str, index: int = 0, *, overrides: dict | None = None):
            request = expand(examples[command][index])
            if overrides:
                request["payload"].update(overrides)
            if log_output and command == 'change.apply':
                request['payload']['operations']['operations'][0]['node']['text'] = 'Weekly watering Ω 😀 "quotes" \\ escapes. ' * 300
            wire = json.dumps(request) + '\n'
            if log_output:
                encoded = subprocess.run([sys.executable, '-I', '-S', str(skill / 'scripts/ndjson_log.py'), '--frame-input'],
                                         input=wire, capture_output=True, text=True, encoding='utf-8', timeout=10)
                if encoded.returncode: raise AssertionError(encoded.stderr)
                for frame in encoded.stdout.splitlines(keepends=True):
                    if len(frame.encode('utf-8')) >= 1000: raise AssertionError('Input frame exceeds terminal-call budget')
                    process.stdin.write(frame)
                    process.stdin.flush()
            else:
                process.stdin.write(wire)
                process.stdin.flush()
            line = response_line(command)
            if not line:
                raise AssertionError(f"Host closed during {command}: {process.stderr.read()}")
            result = json.loads(line)
            if result["status"] != "ok":
                raise AssertionError(f"Bundled {command} example failed: {result}")
            if command != "host.exit" and process.poll() is not None:
                raise AssertionError(f"Host exited before continuing after {command}")
            payload = result["payload"]
            if isinstance(payload, dict) and "reference" in payload:
                bindings["$REF"] = payload["reference"]
                bindings["$SESSION"] = {key: payload["reference"][key] for key in ("projectId", "sessionId")}
            return payload

        try:
            if log_output:
                readiness = process.stderr.readline()
                prefix = "NDJSON ready; responses: "
                if not readiness.startswith(prefix):
                    raise AssertionError(f"Log host did not start: {readiness}")
                response_log = Path(readiness.removeprefix(prefix).rstrip("\n"))
                if response_log != trial['path'] / '.docs-vw.tmp.html':
                    raise AssertionError(f"Unexpected log allocation: {response_log}")
            for command in ("host.help", "project.init", "project.verify", "project.status",
                            "read.node", "read.search", "read.dependencies", "read.context",
                            "change.begin", "change.apply", "change.patch", "change.show"):
                send(command)
                if log_output and command == 'change.apply':
                    shown = send('change.show')
                    claim = next(item['node'] for item in shown['operations']['operations'] if item['entityId'] == 'water-budget')
                    if claim['text'] != 'Weekly watering Ω 😀 "quotes" \\ escapes. ' * 300:
                        raise AssertionError('Framed change.apply altered the large Unicode claim')
            page = send("change.affected")
            evidence = list(page["items"])
            while page["page"]["nextCursor"]:
                page = send("change.affected", overrides={"cursor": page["page"]["nextCursor"]})
                evidence.extend(page["items"])
            affected = {item["value"]["nodeId"] for item in evidence if item["kind"] == "affectedNode"}
            context = {item["value"]["nodeId"] for item in evidence if item["kind"] == "scopeContext"}
            if affected != {"water-budget", "purpose"} or context:
                raise AssertionError(f"Walkthrough review lists drifted: {affected}, {context}")
            send("change.review")
            before_write = document.read_bytes()
            page = send("change.preview")
            while page["reviewPage"]["nextCursor"]:
                bindings["$CURSOR"] = page["reviewPage"]["nextCursor"]
                page = send("change.preview", 1)
            blocked = send("change.agent-write")
            if blocked["status"] != "agentReviewBlocked" or document.read_bytes() != before_write:
                raise AssertionError("Ordinary example bypassed host review")
            # Synthetic data for offline protocol verification, never an actual
            # host reviewer or a claim that this proposal is semantically sound.
            bindings["$HOST_DECISION"] = {
                "decision": "allow", "summary": "Offline example fixture only.", "concerns": [],
            }
            send("change.agent-review")
            written = send("change.agent-write")
            if written["status"] == "unpublished":
                trial["retain"] = True
                raise AssertionError(f"Preserve committed working DB for recovery: {written['workingDbPath']}")
            if written["status"] != "written":
                raise AssertionError(f"Example failed to publish: {written}")
            if not send("project.verify")["isValid"]:
                raise AssertionError("Published example failed verification")
            node = send("read.node", overrides={"entityId": "water-budget"})
            if node["text"] != "Weekly watering is limited to one hour.":
                raise AssertionError("Patched claim was not published")
            if document.read_bytes() == before_write:
                raise AssertionError("Published document did not change")
            if log_output:
                # Close readers before Windows deletes the owned log. The helper
                # can remove it before an exit response is read; use process status.
                if log_reader is not None:
                    log_reader.close()
                    log_reader = None
                process.stdin.write(json.dumps(expand(examples["host.exit"][0])) + "\n")
                process.stdin.flush()
            else:
                send("host.exit")
            process.stdin.close()
            exit_code = process.wait(timeout=COMMAND_TIMEOUT_SECONDS)
            diagnostics = process.stderr.read()
            if exit_code != 0 or diagnostics != "":
                raise AssertionError(f"Example host did not exit cleanly: {exit_code}, {diagnostics}")
            if log_output and response_log.exists():
                raise AssertionError("Example left its owned temporary file")
            if any(document.parent.glob("vw-*-*")):
                raise AssertionError("Example left a temporary workspace")
            if list(document.parent.glob("*.vw-lock")) or list(document.parent.glob("*.vw.db")):
                raise AssertionError("Example left a working DB or lock")
        finally:
            if log_reader is not None:
                log_reader.close()
            if process.poll() is None:
                # Give the host EOF to release its own session and locks first.
                process.stdin.close()
                try:
                    process.wait(timeout=COMMAND_TIMEOUT_SECONDS)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=COMMAND_TIMEOUT_SECONDS)
            for stream in (process.stdin, process.stdout, process.stderr):
                stream.close()
    transport = "live UTF-8 log" if log_output else "pipes"
    restriction = "; hard links denied" if deny_hard_links else ""
    print(f"Bundled author/review/save examples passed via {transport} (offline review fixture{restriction}).")


if __name__ == "__main__":
    verify(Path(sys.argv[1]).resolve(), log_output="--log-output" in sys.argv[2:],
           deny_hard_links="--deny-hard-links" in sys.argv[2:])
