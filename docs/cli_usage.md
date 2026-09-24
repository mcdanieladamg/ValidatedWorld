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
process cleanly. Project, read, template, and AI-status commands mirror the
one-shot surface. Change commands are stateful:

1. `change.begin` opens a verified snapshot and returns an exact reference.
2. `change.apply` replaces the operation batch; `change.patch` updates it.
3. Inspect `change.show`, `change.affected`, and `change.validate` as needed.
   `change.focus` can add explicit scope-parent selections without mutating the
   session. `change.expand` reruns affected analysis with new caller budgets;
   `change.omission-details` pages a fingerprint-bound omission group.
4. `change.review` records dispositions and presented scope context.
5. Call `change.preview`, following every `nextCursor`
   for that exact revision and page size.
6. `change.write` performs one guarded atomic write.
7. Use `change.discard` to abandon the in-memory proposal.

Keep the returned reference from each response and pass it to the next mutating
request. Any proposal or review change makes an earlier reference stale.
Incomplete review, incomplete preview evidence, a stale database fingerprint,
failed graph rules, or an independent-review block leaves the database
unchanged. EOF or process loss discards unfinished sessions.

Snapshots from `change.begin`, `change.show`, `change.apply`, `change.patch`,
`change.review`, and `change.validate` are compact by default: they return
counts and readiness without operation bodies or the proposed graph. Request
`includeOperations` or `includeProposedGraph` only for a deliberate full
inspection. `change.affected` accepts `limit` and `cursor` and returns bounded
`items` pages with `page.nextCursor`, `page.totalCount`, and `page.isComplete`.
Its cursor is bound to the exact session revision and page size. `change.preview`
remains the complete exact review evidence gate and is also paged; follow every
cursor before writing.

For onboarding and routine discovery, verify the database, read its status,
confirm the purpose, then search task terms and inspect only the relevant nodes,
dependencies, scope, and context. Use bounded reads and follow cursors when
additional results matter. Knowledge is added incrementally; a complete graph
import is not required. Do not use `project.open` for routine discovery because
it returns the whole graph.

## Optional independent review

Independent OpenAI review is configured only through environment variables.
`OPENAI_API_KEY` is the shared key; `VW_AIREVIEW__OPENAI__APIKEY` overrides it
for review. Other supported settings include:

```text
VW_AIREVIEW__ENABLED
VW_AIREVIEW__PROVIDER
VW_AIREVIEW__MODEL
VW_AIREVIEW__TIMEOUTSECONDS
VW_AIREVIEW__MAXREQUESTBYTES
VW_AIREVIEW__MAXREQUESTITEMS
VW_AIREVIEW__MAXREQUESTTOKENS
```

Send `ai.status` through NDJSON to inspect nonsecret effective configuration.
When review is enabled and configured, only an `allow` decision bound to the
exact proposal permits the write. Provider failures and malformed responses do
not fall back to an unreviewed write. No provider call is made when review is
disabled or no key is configured.

The separate optional authoring assistant uses the `VW_AIAUTHORING__*`
settings and a distinct Responses API conversation:

```text
ai status
ai assistant <database>
```

It exposes only bounded graph tools, requires search before additions, uses the
ordinary exact-preview/write gates, and never receives an AI-review bypass.
Each provider call is separately billable. An unfinished in-memory proposal is
discarded when the assistant exits.

## Exit codes

| Code | Meaning |
|---:|---|
| 0 | Success |
| 1 | Invalid command, request, or argument |
| 2 | Project or storage-domain failure |
| 3 | Unexpected internal failure |

Structured NDJSON request errors are returned as result lines so a long-lived
process can continue handling later requests.
