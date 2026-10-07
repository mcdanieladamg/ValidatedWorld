# Persistent input/output without work files

Keep one application process alive through authoring, review and save. Proposals,
SQLite state, approvals and packets exist only in that process. A completed
one-shot command cannot supply a live session handle. Use the same interpreter
that passed the launcher's version check.

## Native pipes or live terminal

Launch the bundled `scripts/validated_world.py ndjson` directly with
`-B -X utf8 -u`; retain its stdin/stdout pipes in the host's persistent execution
environment. With Python `subprocess.Popen`, use text pipes and `encoding="utf-8"`;
write one JSON line, flush stdin, and read one complete stdout line per request.
For terminal tools, start a retained interactive process and send subsequent
lines through its live session ID. Read structured response lines from each
tool result; terminal echoes are not responses. Check `host.help` while the
process remains alive before mutation. Allow each command up to 30 seconds.

Do not redirect to a log file or use shell capture that withholds responses until
process exit. Do not parse an incomplete line. If the host cannot retain pipes or
read live terminal responses, use the optional controller below when available.

## Optional in-memory controller

For a host that permits loopback connections but buffers terminal stdout, start:

```text
<python> -B -X utf8 -u <skill>/scripts/validated_world.py serve
```

It announces one JSON object with `controllerUrl` and stays alive. Retain the
actual running process handle and exact URL; never invent a session ID if the
launcher exited. The random controller URL is local and authorizes commands, so
keep it with the authoring controller rather than sharing it with reviewers.

Send each request as a completed command with the JSON envelope on stdin:

```text
<python> -B -X utf8 <skill>/scripts/validated_world.py request <controllerUrl>
```

The client prints one complete response and exits; the server retains the same
application state. Alternatively POST the envelope with Python `urllib.request`
and `ProxyHandler({})`, using a 30-second timeout. All protocol commands and
payloads are unchanged. No logs or temporary files are created. `host.exit`
returns its response and closes the server. An invalid exit request leaves it
alive so the payload can be corrected.

Both transports require the host's ordinary permissions. If loopback is denied,
use native pipes/live NDJSON; do not broaden permissions or change security
controls. If neither transport works, report the missing capability and leave
the change unsaved.

EOF, discard, exit or process loss releases in-memory work. Before save the HTML
remains the last saved state. Saving itself writes directly and may leave partial
HTML if interrupted; follow the skill's save-failure instructions.
