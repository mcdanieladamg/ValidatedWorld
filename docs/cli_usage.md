# ValidatedWorld CLI reference

ValidatedWorld is a local Python 3.12+ command-line application. From a source
checkout, make the package importable and run it with:

```powershell
$env:PYTHONPATH = (Join-Path (Get-Location) 'src-python')
py -3.12 -m validated_world --help
```

An installed package also provides the `validated-world` command. Quote paths
and text containing spaces. Project and backup destinations are never
overwritten.

## Project commands

```text
project init <path> <project-id> <title> <purpose-node-id> <purpose-text>
project status <path>
project open <path>
project verify <path>
project backup <source> <destination>
project export-sql <path>
project diff <base> <target> [--limit N] [--cursor TOKEN]
project merge <base> <ours> <theirs>
project bulk-plan <path> <manifest> [--chunk-size N] [--cursor TOKEN]
```

`project open` returns the complete graph. Prefer bounded reads for discovery.
`project diff`, `project merge`, and `project bulk-plan` are read-only planning
operations; applying a plan still requires the reviewed change workflow.

## Templates and samples

```text
sample list
sample create technical-project <path>

template list
template describe <name-or-template-path>
template export <name-or-template-path> <destination.json>
template instantiate <name-or-template-path> <database> <project-id> <title> <purpose-text>
```

The built-in templates are `code-development` and `research-notebook`.

## Bounded reads

```text
read node <database> <node-id>
read edge <database> <edge-id>
read nodes|edges <database> [--limit N] [--cursor TOKEN]
read search|ranked-search <database> <text> [--limit N] [--cursor TOKEN]
read tag <database> <exact-tag> [--limit N] [--cursor TOKEN]
read scope <database> <node-id> [--limit N] [--cursor TOKEN]
read neighbors|dependencies <database> <node-id> [--limit N] [--cursor TOKEN]
read path <database> <source-node-id> <target-node-id>
read context <database> <node-id[,node-id...]>
read health|report <database> [--limit N]
```

Page cursors are bound to the project fingerprint and query. A cursor from a
different query or snapshot is rejected.

## Artifact checks

Artifact anchors are ordinary graph nodes with kind `external-anchor` or tag
`artifact`. Checks are deny-by-default and read only from explicitly allowed
roots:

```text
artifact check <database> --allow-root <directory> [--allow-root <directory> ...]
```

The check reports matched, drifted, missing, invalid, unauthorized, and
unreadable anchors. It never changes the external file or the graph.

## Persistent NDJSON interface

Run one process for a complete change session:

```powershell
py -3.12 -m validated_world ndjson
```

Each input line is a JSON request and each output line is its JSON result:

```json
{"version":1,"command":"project.verify","payload":{"path":"C:\\work\\project.vw.db"}}
```

Use `host.help` to discover the command catalog and `host.exit` to close the
process cleanly. Project, read, and template commands mirror the
one-shot surface. Change commands are stateful:

1. `change.begin` opens a verified snapshot and returns an exact reference.
2. `change.apply` replaces the operation batch; `change.patch` updates it.
3. Inspect `change.show`, `change.affected`, and `change.validate` as needed.
   `change.focus` can add explicit scope-parent selections without mutating the
   session. `change.expand` reruns complete affected analysis and invalidates review.
   Paging controls presentation; affected analysis has no resource-allocation caps.
4. `change.review` records dispositions and presented scope context.
5. Call `change.preview`, following every `nextCursor`
   for that exact revision and page size.
6. In the skill workflow, a fresh host subagent reviews the exact proposal.
   Submit its allow or block result through `change.agent-review`, then call
   `change.agent-write` for an allow. The direct `change.write` command is the
   explicit human manual review route.
7. Use `change.discard` to abandon the in-memory proposal.

Keep the returned reference from each response and pass it to the next mutating
request. Any proposal or review change makes an earlier reference stale.
Incomplete review, incomplete preview evidence, a stale database fingerprint,
failed graph rules, or an agent-review block leaves the database
unchanged. EOF or process loss discards unfinished sessions.

Snapshots from `change.begin`, `change.show`, `change.apply`, `change.patch`,
`change.review`, and `change.validate` are compact by default: they return
counts and readiness without operation bodies or the proposed graph. Request
`includeOperations` or `includeProposedGraph` only for a deliberate full
inspection. `change.affected` accepts `limit` and `cursor` and returns bounded
`items` pages with `page.nextCursor`, `page.totalCount`, and `page.isComplete`.
Its cursor is bound to the exact session revision and page size. `change.preview`
is the exact evidence gate for manual and single-reviewer writes. Broad reviews
can use lossless packets with a separate synthesis gate instead.

For onboarding and routine discovery, verify the database, read its status,
confirm the purpose, then search task terms and inspect only the relevant nodes,
dependencies, scope, and context. Use bounded reads and follow cursors when
additional results matter. Knowledge is added incrementally; a complete graph
import is not required. Do not use `project.open` for routine discovery because
it returns the whole graph.

## Agent review decision

`change.agent-review` takes `reference` and `decision`. The decision object
contains `decision` (`allow` or `block`), a nonempty `summary`, and `concerns`.
Allow requires an empty concerns array. Block requires at least one concern
with nonempty `code`, `message`, and one or more `citations` of the form
`{"entityId":"stable-id"}`. Cited IDs must occur in the exact proposal.
The returned `agentReview.binding` records the proposal reference.
`change.agent-write` rejects a missing, blocked, or stale decision and then
performs the ordinary atomic write checks. The engine cannot authenticate the
subagent that supplied the decision; the host agent must maintain independence.
No product API key is needed. The host controls model selection and charges.

## Packet review commands

After completing `change.review`, `change.review-plan` returns a paged manifest
with one owner per exact review ordinal, a `planFingerprint`, algorithm, coverage
count and `synthesisPacketId`. `change.review-packet` takes `reference`,
`planFingerprint`, `packetId` and optional `limit`/`cursor`; it returns bound exact
owned evidence plus labeled shared context. Cursors bind content and page size.

`change.review-export` takes the same binding fields, an explicit nonexistent
`destinationPath`, and optional page `limit`. Its compact result points to a
manifest containing safe generated page paths and SHA-256 hashes. Export files
are temporary immutable evidence, not restorable drafts or project authority.
`change.review-cleanup` removes engine-owned exports. Successful writes/discards
and graceful EOF clean them; the host cleans known directories after a crash.

`change.review-result` takes `reference`, `binding` and `result`. Binding contains
`reference`, `planFingerprint`, `packetId`, `packetFingerprint`; result contains
`decision`, `summary`, `citations`, `concerns`, `questions`. All assigned pages must
be presented. Allow has citations but no unresolved concerns/questions; block
requires cited concerns; needs-context requires questions and cannot approve.
Terminal results are immutable. Every branch must allow before synthesis is
available. A separate fresh synthesis reviewer receives global rule/validation
and root evidence, cross-branch edges/endpoints and branch summaries. Only its
current allow together with all branch allows permits `change.agent-write`.

`change.review-context` replaces registered supplements with exact old/new
session evidence for its `entityIds` list and returns a new reference. It
invalidates the entire plan/results. Explicit `refinements` on `change.review-plan`
partition a branch's assigned ordinals losslessly; missing, duplicate or foreign
ordinals fail. Refinement restarts all review. Proposal/disposition/context and
external DB revisions also invalidate approvals. There are no cost, work, byte,
item or token allocations; page sizes and deliberate read-query depth remain.

See the skill's [packet workflow](../skills/validated-world/references/packet-review.md)
for complete host instructions. The engine cannot authenticate reviewer identity,
independence or reading. No API client or credential configuration is added.

## Exit codes

| Code | Meaning |
|---:|---|
| 0 | Success |
| 1 | Invalid command, request, or argument |
| 2 | Project or storage-domain failure |
| 3 | Unexpected internal failure |

Structured NDJSON request errors are returned as result lines so a long-lived
process can continue handling later requests.
