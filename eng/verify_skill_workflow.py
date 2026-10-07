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
        # guessing about unknown files in a developer trial.
        trial["retain"] = True
        raise
    finally:
        if trial["retain"]:
            print(f"Preserved workflow trial for diagnostics: {root}", file=sys.stderr)
        else:
            # Inspect the uniquely allocated trial before recursive cleanup.
            entries = list(root.iterdir())
            if (root.parent != Path(tempfile.gettempdir()).resolve()
                    or not root.name.startswith("vw-workflow-examples-")
                    or any(item.name != "docs-vw.html" or item.is_symlink()
                           or not item.is_file() or item.resolve().parent != root for item in entries)):
                raise AssertionError(f"Refusing unexpected trial cleanup: {root}")
            shutil.rmtree(root)


def verify(skill: Path, *, session_http: bool = False, deny_hard_links: bool = False) -> None:
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
        bindings: dict = {"$PATH": str(document)}
        command = [sys.executable, "-B", "-X", "utf8", "-I", "-S", str(launcher), "serve" if session_http else "ndjson"]
        # Reject every implicit filesystem write in the actual isolated child.
        # Only the human-selected HTML destination is writable by the product.
        bootstrap = '''import os, pathlib, runpy, sys
selected = pathlib.Path(sys.argv[1]).resolve()
def audit(event, arguments):
    if event == "open":
        path, mode, flags = arguments
        writing = (isinstance(mode, str) and any(c in mode for c in "wax+")) or (flags & (os.O_WRONLY | os.O_RDWR | os.O_CREAT))
        if writing and (not isinstance(path, (str, bytes, os.PathLike)) or pathlib.Path(path).resolve() != selected):
            raise PermissionError("fixture rejects implicit file writes: " + str(path))
    if event in {"os.mkdir", "os.rename", "os.link", "os.remove", "os.rmdir", "tempfile.mkdtemp"}:
        raise PermissionError("fixture rejects temporary filesystem operations: " + event)
sys.addaudithook(audit)
sys.argv = sys.argv[2:]
runpy.run_path(sys.argv[0], run_name="__main__")
'''
        command = command[:6] + ['-c', bootstrap, str(document)] + command[6:]
        process = subprocess.Popen(command, cwd=trial["path"], stdin=subprocess.PIPE,
                                   stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8")
        from urllib.request import Request, ProxyHandler, build_opener
        client = build_opener(ProxyHandler({}))
        controller = None

        def pipe_line():
            from queue import Queue, Empty
            from threading import Thread
            response = Queue()
            Thread(target=lambda: response.put(process.stdout.readline()), daemon=True).start()
            try: return response.get(timeout=COMMAND_TIMEOUT_SECONDS)
            except Empty: raise AssertionError(f"Pipe response timed out after {COMMAND_TIMEOUT_SECONDS}s")

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
            if session_http:
                try:
                    with client.open(Request(controller, data=json.dumps(request).encode('utf-8'), headers={'Content-Type': 'application/json'}), timeout=COMMAND_TIMEOUT_SECONDS) as response:
                        line = response.read().decode('utf-8')
                except OSError as exc:
                    raise AssertionError(f"Session response failed during {command}: {exc}") from exc
            else:
                process.stdin.write(json.dumps(request) + "\n"); process.stdin.flush()
                line = pipe_line()
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
            if session_http:
                readiness = pipe_line()
                controller = json.loads(readiness)["controllerUrl"]
            for command in ("host.help", "project.init", "project.verify", "project.status",
                            "read.node", "read.search", "read.dependencies", "read.context",
                            "change.begin", "change.apply", "change.patch", "change.show"):
                send(command)
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
            if written["status"] != "written":
                raise AssertionError(f"Example failed to publish: {written}")
            if not send("project.verify")["isValid"]:
                raise AssertionError("Published example failed verification")
            node = send("read.node", overrides={"entityId": "water-budget"})
            if node["text"] != "Weekly watering is limited to one hour.":
                raise AssertionError("Patched claim was not published")
            if document.read_bytes() == before_write:
                raise AssertionError("Published document did not change")
            send("host.exit")
            process.stdin.close()
            exit_code = process.wait(timeout=COMMAND_TIMEOUT_SECONDS)
            diagnostics = process.stderr.read()
            if exit_code != 0 or diagnostics != "":
                raise AssertionError(f"Example host did not exit cleanly: {exit_code}, {diagnostics}")
            if list(document.parent.iterdir()) != [document]:
                raise AssertionError("Example created files or directories beyond its HTML document")
        finally:
            if process.poll() is None:
                # Give the host EOF to release its own session and locks first.
                process.stdin.close()
                if session_http: process.terminate()
                try:
                    process.wait(timeout=COMMAND_TIMEOUT_SECONDS)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=COMMAND_TIMEOUT_SECONDS)
            for stream in (process.stdin, process.stdout, process.stderr):
                stream.close()
    transport = "in-memory controller" if session_http else "pipes"
    restriction = "; hard links denied" if deny_hard_links else ""
    print(f"Bundled author/review/save examples passed via {transport} (offline review fixture; only selected HTML writable{restriction}).")


if __name__ == "__main__":
    verify(Path(sys.argv[1]).resolve(), session_http="--session-http" in sys.argv[2:],
           deny_hard_links="--deny-hard-links" in sys.argv[2:])
