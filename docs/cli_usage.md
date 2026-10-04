# ValidatedWorld manual command reference

This standalone reference covers optional direct/manual command use: you, a
local project database, and Python. You author the edits, inspect their
consequences, and save the reviewed proposal yourself. The application does not
call model APIs, accept model API keys, or launch agents or subagents.

The normal product workflow is to ask an agent using the ValidatedWorld skill to
manage the database. That host supplies any subagents separately. These manual
instructions require no agent host or agent review results.

ValidatedWorld runs locally with Python 3.12+. From a source
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

Authoring, consequence review, your review acknowledgments and saving all happen
sequentially in this same terminal and process. No second window or reviewer is
required. The current interface accepts NDJSON requests; it does not display an
interactive yes/no approval prompt. You acknowledge the evidence with
`change.review` and save with `change.write` after reading the complete preview.

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
3. Inspect `change.show` and `change.validate` as needed. Page `change.affected`
   completely and read every changed node, downstream consequence, changed edge,
   and required upstream scope context. Repair stale claims in the same batch
   with `change.patch`, then inspect the refreshed evidence.
   `change.focus` can add explicit scope-parent selections without mutating the
   session. `change.expand` reruns complete affected analysis and invalidates review.
   Paging controls presentation; affected analysis has no resource-allocation caps.
4. `change.review` records your dispositions for every affected node and the
   scope-context IDs you have read. Use `updated` for directly edited nodes,
   `reviewedNoChange` when an affected claim remains correct, or `notApplicable`
   with a rationale. Unreviewed consequences keep the proposal pending.
5. Call `change.preview`, following every `nextCursor`
   for that exact revision and page size.
6. Call `change.write` to save the completely reviewed and previewed proposal
   atomically. This manual route requires no agent decision and launches no agent.
7. Use `change.discard` to abandon the in-memory proposal.

Keep the returned reference from each response and pass it to the next mutating
request. Any proposal or review change makes an earlier reference stale.
Incomplete review, incomplete preview evidence, a stale database fingerprint,
or failed graph rules leave the database
unchanged. EOF or process loss discards unfinished sessions.

Snapshots from `change.begin`, `change.show`, `change.apply`, `change.patch`,
`change.review`, and `change.validate` are compact by default: they return
counts and readiness without operation bodies or the proposed graph. Request
`includeOperations` or `includeProposedGraph` only for a deliberate full
inspection. `change.affected` accepts `limit` and `cursor` and returns bounded
`items` pages with `page.nextCursor`, `page.totalCount`, and `page.isComplete`.
Its cursor is bound to the exact session revision and page size. `change.preview`
is the exact evidence gate for manual writes. Read every page yourself, including
downstream claims and upstream context; the application presents evidence and
checks recorded completeness, but cannot prove that you read it or judged it correctly.

For onboarding and routine discovery, verify the database, read its status,
confirm the purpose, then search task terms and inspect only the relevant nodes,
dependencies, scope, and context. Use bounded reads and follow cursors when
additional results matter. Knowledge is added incrementally; a complete graph
import is not required. Do not use `project.open` for routine discovery because
it returns the whole graph.

The shared `host.help` catalog also lists host-integration commands for accepting
externally supplied review decisions and preparing evidence packets. Those commands
only handle local evidence and submitted data; they cannot invoke a reviewer.
They are outside this manual workflow and are documented in the
[skill workflow](../skills/validated-world/SKILL.md) and its
[packet reference](../skills/validated-world/references/packet-review.md).

## One-update dependency skip

For a rare graph cleanup with fully understood consequences, set `"skipDependencies": true` on
`change.apply` or `change.patch`. The whole operation batch may add, replace, or
remove multiple nodes and edges. The preview contains only those edits with
their exact before/after values. No upstream scope context, dependent-node
traversal, dispositions, unrelated rule evidence, or independent reviewer is
required. Page `change.preview` completely, then call `change.write` to save
the batch atomically.

Skipping dependencies is highly discouraged for routine updates: connected
claims can become stale without being reviewed. Use it only when there are no
downstream consequences, or all consequential edits are specific, already known
and included in the batch. Uncertain impacts require normal review. The size of
a review alone does not justify skipping. Structural validity, active graph rules and
stale-write protection still apply. The mode is bound to the exact proposal and
resets on every later apply, patch, expand, or new session unless explicitly
selected again. The snapshot and preview report `skipDependencies`.

The immediate direct command accepts the same strict operation-batch JSON
(`{"operations":[...]}`) used inside an NDJSON `operations` payload:

```text
change write <database> <operation-batch.json> --skip-dependencies
```

Review that file before invoking the command. It prints paged before/after edits
and saves the whole batch immediately, then prints the write result. For a
separate preview and save, use the NDJSON workflow. Omitting the flag cannot
perform this exceptional direct write. No justification ledger or persistent
bypass setting is created.

## Exit codes

| Code | Meaning |
|---:|---|
| 0 | Success |
| 1 | Invalid command, request, or argument |
| 2 | Project or storage-domain failure |
| 3 | Unexpected internal failure |

Structured NDJSON request errors are returned as result lines so a long-lived
process can continue handling later requests.
