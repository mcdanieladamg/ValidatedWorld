# Keep requests and responses live

The NDJSON host flushes each result before reading the next request. Shell
pipelines/redirection and command-result capture can still buffer output until
exit. Closing the process to flush it loses unfinished proposals. Do not parse
an empty capture or wait for EOF to receive an individual response.

## Terminal tools with retained stdin

Use the bundled optional `scripts/ndjson_log.py` when terminal output is buffered
or includes echoes, ANSI controls or wrapping. It runs the same launcher in one
process and writes raw responses directly to a UTF-8 file, flushing each line.
It adds no network service, model client, durable draft or review bypass.

Start a live interactive terminal session with the selected interpreter and
set the execution tool's working directory to the identified, authorized
project folder. The helper creates a unique `vw-ndjson-*` directory under
`tmp/validated-world/` there by
default, in the same process that opens its log. No log-path argument or
separate directory-creation command is needed:

```text
<python> -X utf8 -u <skill>/scripts/ndjson_log.py
```

Quote shell arguments containing spaces. Use the host's session-capable
execution tool. Retain a session handle only when the result actually supplies
one for a still-running process; an exited command has no usable session handle.
Send later input through the host's stdin/write tool. The helper announces
`NDJSON ready; responses: <absolute-path>` on stderr. Use that actual path,
inside the selected project folder. Send one request plus newline:

```json
{"version":1,"command":"host.help","payload":{}}
```

Read the log with a separate filesystem/read command **while the session is
still running**, before initializing or changing a project. Confirm that the
`host.help` response has `status: "ok"` and the process remains alive. This
checks both the session handle and cross-command log visibility.
Each complete newline-terminated line is one response object.
Track consumed lines or a byte offset and parse only new complete lines. A file
read may race with a write: keep an incomplete final line for the next read;
an empty read means no complete response yet. Wait briefly/check the live
session rather than parsing empty text or closing it. Do not parse terminal
echoes or readiness messages as protocol JSON. Serialize paths and requests
with a JSON library; see [payload templates](command-workflow.md).

The default uses the shared project folder rather than OS temp, whose visibility
can differ between sandboxed executions. If log allocation or the live response
check fails, report the diagnostic and stop before project changes; the helper
does not fall back to another directory. Do not change sandbox settings or
request broader access just to store logs. The log is temporary transport
evidence, not project storage; keep `docs-vw.html` outside the log directory.
Report inaccessible temporary paths that cannot be cleaned up.

For callers with a deliberately separate shared working area, `--temp-root`
accepts an absolute authorized root instead of the working directory. An
explicit absolute response-log argument also remains supported for a
caller-owned temporary directory already visible to the host; its parent must
exist and the helper refuses an existing log. These overrides are not needed
for the ordinary skill workflow.

Keep this same session for `change.begin`, authoring, evidence, host review and
`change.agent-write`. Reviewer tools may run separately while it waits. The log
is temporary evidence, not a way to resume session references after process
loss. On a crash, verify the existing project and begin a fresh proposal;
preserve any reported committed-unpublished working DB for recovery.

Read save results and all required review evidence while the session is alive.
After completing or discarding the proposal, close any external log readers,
send `host.exit` with `payload: {}`, and confirm process exit code zero. The
helper automatically removes its owned log and directory on normal exit or EOF,
including after a structured request error. Its final exit response may be
removed before a separate read; use process status to confirm shutdown.
The shared `tmp/validated-world/` parent remains. Database workspaces and default
review-evidence exports are also grouped there; cleanup never sweeps that parent.

Use `--keep-log` only when diagnostics are explicitly needed after shutdown.
Caller-supplied logs are also retained. Abnormal exits preserve diagnostics;
forced termination may leave temporary directories because cleanup cannot run.
Cleanup refuses unexpected or replaced files and reports the exact remaining
path on failure. Inspect retained contents before removing only known owned
files. Never delete the project or a recovery DB as log cleanup.

## Hosts with a persistent Python execution environment

If the host can retain a Python object across tool calls, ordinary pipes also
work. Keep this process object in that environment:

```python
import json
import subprocess

process = subprocess.Popen(
    [python_executable, "-X", "utf8", launcher_path, "ndjson"],
    stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    text=True, encoding="utf-8",
)

def request(command, payload):
    process.stdin.write(json.dumps({"version": 1, "command": command,
                                   "payload": payload}) + "\n")
    process.stdin.flush()
    line = process.stdout.readline()
    if not line:
        raise RuntimeError("NDJSON host closed before responding")
    return json.loads(line)
```

Use `readline()` for one response; reading the whole stdout stream waits for
EOF. This is not persistent if each tool call starts a new Python interpreter.
Send `host.exit` only after saving/discarding, then close streams and wait for
exit. Hosts must supply retained process control; command-only tools that
cannot keep stdin open cannot carry a process-local change session.
