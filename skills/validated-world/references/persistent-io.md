# Persistent authoring sessions

Keep one ValidatedWorld session alive through authoring, review and saving. Use
the verified Python executable and this skill's absolute launcher path. Launch
in the selected HTML's containing folder, or its intended folder for a new
project. The folder must exist. Resolve that selected folder's physical path
before launching so OS directory aliases use one consistent spelling.

The session fixes this folder as `projectRoot`. File arguments, custom templates,
manifests, backups, exports and artifact allowed roots must stay within it or its
subfolders. Use root-relative paths or absolute paths with the same physical
spelling. Requests cannot enlarge the root; do not select a broader parent to
work around a rejected path.

## Preferred: Python subprocess pipes

Run this recipe once in the host's persistent Python environment. Supply
`python_executable`, `launcher_path` and `project_folder` from the verified
runtime and selected project. It uses Python's standard library only.

```python
import json
from pathlib import Path
from queue import Empty, Queue
import subprocess
import sys
from threading import Thread

project_folder = str(Path(project_folder).resolve())
sys.path.insert(0, str(Path(launcher_path).parent))
from review_handoff import ReviewFiles, compact
review_files = ReviewFiles(project_folder)
process = subprocess.Popen(
    [python_executable, "-B", "-X", "utf8", "-u", launcher_path, "ndjson"],
    cwd=project_folder,
    stdin=subprocess.PIPE,
    stdout=subprocess.PIPE,
    text=True,
    encoding="utf-8",
)
responses = Queue()

def read_responses():
    for line in process.stdout:
        responses.put(line)
    responses.put(None)

Thread(target=read_responses, daemon=True).start()

def request(command, payload):
    envelope = {"version": 1, "command": command, "payload": payload}
    process.stdin.write(json.dumps(envelope) + "\n")
    process.stdin.flush()
    try:
        line = responses.get(timeout=30)
    except Empty:
        process.terminate()
        raise TimeoutError("ValidatedWorld did not reply within 30 seconds")
    if line is None:
        raise RuntimeError("ValidatedWorld exited before replying; check host stderr")
    result = json.loads(line)
    if result["command"] != command:
        raise RuntimeError("Unexpected response; stop this session")
    if result["status"] != "ok":
        raise RuntimeError(result)
    return result

def close_session():
    try:
        if process.poll() is None:
            request("host.exit", {})
    finally:
        try:
            process.wait(timeout=30)
        except subprocess.TimeoutExpired:
            process.terminate()
            try:
                process.wait(timeout=30)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=30)
        process.stdin.close()
        process.stdout.close()
        warnings = review_files.close()
        if warnings:
            print({"reviewFileCleanupWarnings": warnings})
```

Call `request("host.help", {})` first. Verify its `payload.projectRoot` matches
the selected folder before mutation. Use [command payloads](command-workflow.md)
for later requests. A `status: error` exception contains the returned diagnostic;
a rejected request can be corrected in the same live session unless the human
requested stopping on errors. After EOF, timeout or an unexpected response, close
the session and start a fresh proposal against the verified saved document.

Retain this same process and `request` function across tool calls. Starting a
new interpreter for each request loses the session. Keep complete response
objects in Python, display bounded portions for inspection, and preserve exact
`payload.reference` objects after mutations. A successful request is not proof
of saving: check the write's `payload.status`, verify the HTML and read back
changed IDs. After saving or discarding, call `close_session()`.

Use `result = request(...)` followed by `print(compact(result))` for routine
output. Keep `result` unchanged for later commands. Do not print full snapshots,
affected pages, ownership manifests, packet pages or a save's collection of
reviewer decisions into the orchestrator's context. Inspect only the records,
diagnostics or questions needed for its current decision. Use the bundled
`review_files` helper for workers to read complete evidence independently.
After a reviewed save or discard and the required worker dialogue, call
`review_files.close()`; report any returned cleanup warnings. `close_session()`
also performs this cleanup on handled failures.

## Hosts with retained interactive terminals

If no persistent Python environment is exposed, start the verified Python with
`-B -X utf8 -u -i -q` in a retained interactive terminal. Execute the recipe once
there, using `exec(complete_recipe_string)` when the terminal sends individual
lines. Retain the actual terminal session ID and submit later `request(...)`
calls to that same interpreter. A tool call that launches and closes a new Python
process cannot serve this purpose.

Parse the child's stdout inside Python before displaying results. Terminal
echoes, prompts, wrapping and cursor-control sequences are not protocol data.
Do not add a separate Node or .NET runtime to manage the session. Give fresh
reviewers the file handoffs in [packet review](packet-review.md); do not
assume another agent can use the author's terminal handle.

## Optional: local HTTP controller

When the host cannot retain Python pipes but permits a retained server and
loopback requests, launch from the selected project folder:

```text
<python> -B -X utf8 -u <skill>/scripts/validated_world.py serve
```

It announces `controllerUrl` and `projectRoot`. Verify the root before mutation
and retain the exact URL and actual running server handle. The secret controller
URL authorizes commands; keep it with the author rather than sharing it with
reviewers. Send each JSON request envelope on stdin to a completed client call:

```text
<python> -B -X utf8 <skill>/scripts/validated_world.py request <controllerUrl>
```

The client returns one complete response; the server retains the session. Python
`urllib.request` with `ProxyHandler({})` and a 30-second timeout also works.
Payloads are unchanged. `host.exit` returns its response and closes the server;
a rejected exit request leaves it usable for correction.

If the host cannot set a working directory, pass the selected folder as the
sole positional argument to `ndjson` or `serve`. If these transports are
unavailable, report the host limitation and leave the change unsaved. Do not
install another runtime or broaden permissions to make them work.
