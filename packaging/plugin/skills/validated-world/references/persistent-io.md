# One retained authoring session

Use the existing Python 3.11+ executable, this skill's absolute directory and the
selected HTML's containing folder. Run the following once in the host's retained
Python environment. When only a retained terminal is available, start Python
with `-B -X utf8 -u -i -q`, then execute this block there. Send all later calls to
that same actual terminal handle. Do not generate a script file.

```python
import sys
sys.dont_write_bytecode = True
sys.path.insert(0, skill_directory + "/scripts")
from session import Session
session = Session(python_executable, project_folder)
```

`Session` starts the public launcher, confirms its project root, retains complete
NDJSON responses in `session.responses`, updates references after every mutation,
and closes its child on request. No sockets, URLs or provider API are involved.
`session.call(command, **payload)` returns a compact summary.
`session.request(command, payload)` returns the complete response for targeted
inspection; unwrap `['payload']` only at use sites. The helper supplies current
`reference`, session locator and review-plan fingerprint; do not copy stale ones
from earlier outputs. Explicit wrong bindings still reject, never auto-approve.

Use `session.begin(path, intent)` after creation/verification. The path is relative
to the selected folder or absolute inside it; requests cannot expand access.
Use the manual command reference for ordinary queries/operations; its illustrative
`$REF`/`$SESSION` fields are omitted when using this helper. No manual unwrap or
alternative response wrapper is needed. A command error can be corrected in the
same live session. EOF or process loss requires a fresh proposal; the companion
file is not a checkpoint. Timeout closes the child and reports failure.

For example, read `node = session.request("read.node", {"path": path,
"entityId": "purpose"})["payload"]`, modify the intended DTO, then send its
operation with `session.call("change.patch", operations={"operations": [...]})`.
Page `change.affected` with the same limit and `payload.page.nextCursor` until
null. Record complete dispositions with `change.review`. `session.assignments()`
pages the ownership plan internally and returns branch IDs. `session.export_batch()`
exports all branches serially before workers start; descriptors share one companion
path with distinct assignment IDs. Freeze that file while read-only workers run.
`session.export(id)` returns a companion path and assignment descriptor for
affected authoring or synthesis. `session.export("affected")`
provides authoring evidence before terminal review. No evidence bodies cross the
author's context through these methods.

After every fresh branch reply, `session.submit(reply)` preserves its complete
binding, receipt and result. Export `synthesis` only after branches allow, then
submit its separate fresh review. A mutation requires fresh evidence and reviews.
For refinement pass `refinements` to `session.assignments`; use current owned
ordinals from bounded plan queries and cover each exactly once.

Save with `session.call("change.agent-write")`, check written status, verify and
read back. To abandon, `session.call("change.discard")`. Finally use
`session.close()`. This clears RAM and removes the owned companion. Recognized
abandoned companion files are removed by a fresh `begin`; live owners, unknown
files, links and changed contents are preserved/rejected with a diagnostic.
The existing process handle must remain alive through writing. If the host
cannot retain a Python environment/terminal or read project files, report that
actual capability failure; do not create controller files or alternate transports.
