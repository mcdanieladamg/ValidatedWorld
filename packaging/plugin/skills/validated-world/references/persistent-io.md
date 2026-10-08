# Keep requests and responses live

The NDJSON host flushes each result before reading the next request. Closing the
process loses unfinished proposals. Shell capture can buffer output until exit;
do not parse terminal echoes or close the host to receive a response.

## Terminal tools with retained stdin

Start the bundled helper with Python 3.11+ in the authorized project folder:

```text
<python> -X utf8 -u <skill>/scripts/ndjson_log.py --document <selected-html>
```

Quote paths containing spaces. The default document is `docs-vw.html` in that
folder. For a custom document, always pass its selected path. Retain only a
session handle actually returned for a running process. The helper announces
`NDJSON ready; responses: <absolute-path>` on stderr. It creates one hidden
`.<document-stem>.tmp.html` sibling (`design.html` uses `.design.tmp.html`), shared by live
responses, review evidence and publication staging. It does not restore drafts
or authorize another session.

Send one request and newline through the host's retained stdin tool:

```json
{"version":1,"command":"host.help","payload":{}}
```

For a long request, use **input frames**, rather than pasting one long terminal
line. The helper buffers them in memory and dispatches one complete request only
after its SHA-256 matches. It creates no additional files and does not split the
semantic operation batch. Direct launcher `ndjson` accepts ordinary NDJSON only;
these frames belong to `ndjson_log.py`.

Generate frames with the bundled helper's `request_frames(request)` in a host
Python execution tool:

```python
import runpy
frames = list(runpy.run_path(helper_path)["request_frames"](request))
```

Here `request` is the complete request object and `helper_path` is the installed
`scripts/ndjson_log.py`. Return the frames to the agent for separate stdin calls;
run this in the host's Python execution tool, not at the live engine prompt.
Alternatively, pipe NDJSON through its file-free encoder:

```text
<python> <skill>/scripts/ndjson_log.py --frame-input [--chunk-size 600]
```

The encoder reads stdin and prints `@vw-begin`, `@vw-part <fragment>` lines and
`@vw-end <sha256>`. Its default fragments are 600 ASCII characters, including
escaped Unicode; each complete frame is below 1,000 bytes. **Send each generated
line in its own retained-stdin tool call**, including its newline. Do not combine
the frames into one oversized call. Choose a smaller `--chunk-size` if the host's
limit requires it. Preserve every character, including spaces and escapes;
terminal visual wrapping is not an extra newline to copy.

There is no engine response until the end frame. Read the ordinary command result
through `--read`. A missing, truncated, duplicated or reordered fragment causes
an `input.frame` error instead of a partial mutation. Resend the complete request
from `@vw-begin`; `@vw-cancel` discards an unfinished input buffer. EOF discards
unfinished input and cannot restore a draft. Do not enter Python code at the live
helper prompt or restart the engine between frames. If ordinary NDJSON is split
across tool calls instead, include its newline only in the final call; framing
also avoids physical terminal line limits and detects altered input.

Read the announced file through a separate helper invocation while the host lives:

```text
<python> <skill>/scripts/ndjson_log.py --read <announced-path> --offset 0
```

This returns `responses`, `nextOffset` and `busy`. Check the live `host.help`
response has `status: "ok"` before project mutation. Save `nextOffset` and pass
it on the next read to receive only new complete response objects. Empty results
or `busy: true` mean check again briefly; neither means approval or process loss.
The reader excludes publication windows, skips internal recovery records and
retains incomplete final lines. Do not keep a raw file handle open: Windows
readers can prevent the atomic replacement. The internal file is temporary
transport, sometimes a staged HTML candidate, and is not another project authority.

Keep the same live session for authoring, review, dialogue and save. Independent
reviewers can inspect committed project context concurrently; competing writers
to the same document are rejected as busy. Complete packet pages are already
returned through this transport; pass exact evidence to fresh workers without
creating separate evidence files. Bind every decision to current evidence.

No shared-directory creation is needed. Allocation errors stop startup without
falling back elsewhere. `--temp-root` selects an explicit authorized shared folder;
a caller-supplied absolute log path remains available for deliberate diagnostics
and never overwrites an existing file. Caller-owned logs remain caller-owned.

Read save results and required evidence before sending `host.exit` with an empty
payload, then confirm exit code zero. Normal helper exit/EOF removes its owned
scratch file, including after structured request errors. The final exit response
may already be gone. `--keep-log` retains requested diagnostics; abnormal exits
also retain data. Fresh startup reclaims only recognized complete abandoned
transport records after acquiring the document's scratch lease. It preserves
unknown/replaced files, active sessions and unpublished recovery. Exact paths
are reported for cleanup failures; never delete a recovery snapshot as log cleanup.

An `unpublished` save reports its recovery path as `workingDbPath`. Preserve that
hidden file and use `project retry-export <reported-path>` after resolving the
cause. Never repeat the semantic change. Changed source bytes block retry. A
crash during final staging may leave a complete HTML candidate requiring
inspection and reconciliation; fresh startup preserves it.

## Hosts with persistent Python execution

Ordinary pipes also work when a host can retain a process object across calls:

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

Use `readline()` for one response; whole-stream reads wait for EOF. This is not
persistent if every tool call creates a new interpreter. Send `host.exit` only
after saving/discarding, close streams and wait for exit.
