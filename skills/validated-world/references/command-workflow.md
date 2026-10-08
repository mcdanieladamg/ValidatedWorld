# Command payloads: create, author, review and save

Use this reference for ordinary skill-led changes. Start the selected Python
executable with this skill's `scripts/validated_world.py ndjson` and keep that
process alive through the write. Send one JSON object per line and read its
result before the next request. Serialize JSON rather than hand-escaping paths.
Use [persistent input/output](persistent-io.md) for a live terminal session and
direct response log, or retained Python pipes. Reading responses must not
require closing the session.
Top-level launcher `--help` describes one-shot syntax; `read --help` is not a
payload-schema command. `host.help` supplies names, not schemas:

```json
{"version":1,"command":"host.help","payload":{}}
```

The following blocks are request **templates**. Replace `$PATH` with the actual
absolute HTML path and adapt the garden's identity and claims to the human's
request. Replace the entire quoted `$REF` value with the latest returned
`payload.reference` **object**, never a string or invented fingerprints.
Replace `$SESSION` with an object containing only `projectId` and `sessionId`
from that reference. Replace `$CURSOR` with the returned cursor string.
`$HOST_DECISION` is the unchanged fresh reviewer's decision object.
These names are example notation, not variables understood by the CLI.

Every result has `version`, `command`, `status` and `payload`. On `status: error`,
the diagnostic is `payload.code` / `payload.message`. Unknown fields, omitted
required fields and duplicate JSON keys are rejected. A rejected read-only
request can be corrected in the same process; honor explicit stop-on-error
instructions. A successful protocol result alone does not establish a saved
change: inspect the write's own `payload.status` below.

## Create and retrieve

If the intended document does not exist, create its purpose root. If it exists,
reuse it after verifying its identity; do not initialize over it.

```json
{"version":1,"command":"project.init","payload":{"path":"$PATH","projectId":"garden","title":"Garden plan","purposeNodeId":"purpose","purposeText":"Plan a garden with limited water."}}
```

Creation also works without NDJSON: invoke the launcher with
`project init <html> <id> <title> purpose <purpose-text>`, quoting each shell
argument as needed. No help/discovery request is a prerequisite.

```json
{"version":1,"command":"project.verify","payload":{"path":"$PATH"}}
```

```json
{"version":1,"command":"project.status","payload":{"path":"$PATH"}}
```

```json
{"version":1,"command":"read.node","payload":{"path":"$PATH","entityId":"purpose","expectedProjectId":"garden"}}
```

```json
{"version":1,"command":"read.search","payload":{"path":"$PATH","text":"water","limit":5,"expectedProjectId":"garden"}}
```

```json
{"version":1,"command":"read.dependencies","payload":{"path":"$PATH","entityId":"purpose","limit":5,"expectedProjectId":"garden"}}
```

```json
{"version":1,"command":"read.context","payload":{"path":"$PATH","nodeIds":["purpose"],"expectedProjectId":"garden"}}
```

For `read.nodes` / `read.edges`, use `path`, optional `limit` / `cursor` /
`expectedProjectId`. `read.tag` adds required `tag`; `read.scope` adds required
`nodeId` and optional `maxDepth`; `read.neighbors` uses `entityId`.
`read.ranked_search` uses the same fields as `read.search`. Paged read results
use `payload.nextCursor`; send it as `payload.cursor` with the same query/limit
until null when further evidence matters. Context results contain
`contextNodes` and `omissions`; investigate relevant omissions.

## Author one proposal

```json
{"version":1,"command":"change.begin","payload":{"path":"$PATH","projectId":"garden","author":"project-agent","intent":"Record the agreed weekly watering budget."}}
```

Retain the returned reference. This example adds a claim and its required scope
parent together. `operations` is an object containing an `operations` array,
not an array directly. Every operation includes both `node` and `edge`, setting
the unused one to null. Node/edge objects require all shown fields.

```json
{"version":1,"command":"change.apply","payload":{"reference":"$REF","operations":{"operations":[{"kind":"add","entityKind":"node","entityId":"water-budget","node":{"id":"water-budget","text":"Weekly watering is limited to two hours.","kind":"constraint","tags":["topic:water"],"attributes":[]},"edge":null},{"kind":"add","entityKind":"edge","entityId":"water-budget-scope","node":null,"edge":{"id":"water-budget-scope","source":"water-budget","target":"purpose","relationship":"scope-parent","reviewDirection":"none","rationale":null,"tags":[],"attributes":[]}}]}}}
```

`change.apply` replaces the whole proposal batch against the original project.
`change.patch` applies edits to the current proposed graph and recomputes the
batch against the original. To revise this still-unpublished new node, patch a
**replace** operation with the same ID; the earlier edge addition remains:

```json
{"version":1,"command":"change.patch","payload":{"reference":"$REF","operations":{"operations":[{"kind":"replace","entityKind":"node","entityId":"water-budget","node":{"id":"water-budget","text":"Weekly watering is limited to one hour.","kind":"constraint","tags":["topic:water"],"attributes":[]},"edge":null}]}}}
```

For an existing entity, `replace` supplies the full final node/edge object,
preserving its other fields. `remove` supplies the ID with both objects null;
remove incident edges in the same batch when needed. Scope-parent edges use
`none`. Dependency edges use `sourceToTarget`, `targetToSource` or `both` for
the actual review direction and an explanatory rationale; see
[graph authoring](graph-authoring.md). Keep existing typed attributes in the
public DTO shape returned by reads, rather than copying HTML record syntax.

After apply/patch, replace `$REF` with the new reference. Read all consequences
and upstream context, including before/after values:

```json
{"version":1,"command":"change.affected","payload":{"session":"$SESSION","limit":5}}
```

These results use `payload.items` and `payload.page.nextCursor`. Repeat with
the same session/limit and the returned `cursor` until null. A cursor is bound
to its revision and page size. Repair stale consequences before continuing.

```json
{"version":1,"command":"change.show","payload":{"session":"$SESSION","includeOperations":true}}
```

## Acknowledge evidence and obtain fresh review

For this isolated example the affected nodes are `water-budget` and `purpose`:
adding the scope edge also selects its parent for review. The purpose is already
in affected evidence, so there are no separate scope-context IDs to acknowledge.
In a real proposal, derive the complete lists from all affected pages.
Use `updated` for direct changes, `reviewedNoChange` for correct
unchanged dependents, or `notApplicable` with a specific `rationale`.

```json
{"version":1,"command":"change.review","payload":{"reference":"$REF","dispositions":[{"nodeId":"water-budget","kind":"updated"},{"nodeId":"purpose","kind":"reviewedNoChange"}],"presentedContextNodeIds":[]}}
```

Retain the new reference, then inspect every exact preview page:

```json
{"version":1,"command":"change.preview","payload":{"reference":"$REF","limit":5}}
```

```json
{"version":1,"command":"change.preview","payload":{"reference":"$REF","limit":5,"cursor":"$CURSOR"}}
```

Follow `payload.reviewPage.nextCursor` until null; keep the same reference and
limit. `allEvidencePresented` means emitted, not reviewed. Give a fresh read-only
subagent the intent, exact reference and every preview page. It returns an
object with exactly `decision` (`allow` / `block`), nonempty `summary`, and
`concerns`. Allow requires an empty concerns array. Block requires concerns shaped
as `{"code":"...","message":"...","citations":[{"entityId":"water-budget"}]}`
using IDs present in the evidence. Never fabricate approval or change a block
into allow. Broader proposals use [packet review](packet-review.md) instead.

Submit the actual unchanged reviewer result:

```json
{"version":1,"command":"change.agent-review","payload":{"reference":"$REF","decision":"$HOST_DECISION"}}
```

Retain the response's reference. Proposal/context/disposition changes invalidate
previous evidence and approval; refresh them before trying to save.

## Save, verify and close

```json
{"version":1,"command":"change.agent-write","payload":{"reference":"$REF"}}
```

Inspect `payload.status`: `written` means published; `agentReviewBlocked` means
unsaved. `unpublished` means the reviewed SQLite snapshot was retained in the hidden
`.tmp.html` file but HTML publication failed: retain
`workingDbPath` and follow the skill's publication recovery instructions without
repeating the semantic mutation. Do not substitute manual `change.write` to
bypass the skill's host review. Verify the file and read back changed IDs with
the earlier `project.verify` / `read.node` payloads; report its absolute path.
To abandon a draft, use `change.discard` with `reference: $REF`.

```json
{"version":1,"command":"host.exit","payload":{}}
```

EOF loses unfinished process-local proposals. Closing the process after a
successful write leaves the published document intact.
