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
Execute it in the retained interpreter; no generated controller script,
proposal file, response log, evidence file or working folder is permitted.
The memory helper is initialized before authoring; keep this controller alive.

```python
import json
from pathlib import Path
from queue import Empty, Queue
import subprocess
import sys
from threading import Thread

project_folder = str(Path(project_folder).resolve())
sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(launcher_path).parent))
from review_memory import ReviewMemory, compact, response
review_memory = ReviewMemory()
protocol_responses = []
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
    protocol_responses.append(result)
    return response(result, command)

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
        review_memory.close()
        protocol_responses.clear()
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
`review_memory` helper to retain complete evidence in RAM. Its methods take this
exact `request` function returning the full protocol envelope. Do not pass a
payload-only or compact wrapper. Unwrap `result["payload"]` only at individual
use sites. `review_memory.message(descriptor)` is an explicit delivery object,
not routine output. Follow [packet review](packet-review.md) to assess a real
host route before exporting it. Clear memory after save/discard and dialogue.

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
reviewers complete evidence through [packet review](packet-review.md); do not
assume another agent can use the author's terminal handle.

If the host cannot set a working directory, pass the selected folder as the
sole positional argument to `ndjson`. If the host cannot retain an authoring
process, report that limitation and leave the change unsaved. Do not create a
controller script or install another runtime. Native reviewer channels are
read-only and described in [packet review](packet-review.md).
