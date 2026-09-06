# ValidatedWorld CLI usage

ValidatedWorld is a local, headless .NET 10 command-line application.
One-shot commands cover project storage and bounded reads. Long-lived change
sessions have two interfaces over the same Application behavior:

- `shell <database>` is the stateful flag-based interface. It remembers the
  selected entity, pending operation batch, review state, and fingerprints.
- `ndjson` is the strict structured interface for AIs, scripts, and integrations.

Both retain unfinished changes only in the running process.

## Run or publish

From the repository root, run a command directly with:

```powershell
dotnet run --project src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj -- <arguments>
```

Or create a framework-dependent release directory and run the produced
executable:

```powershell
dotnet publish src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj `
    -c Release -o artifacts/validated-world
./artifacts/validated-world/ValidatedWorld.Cli.exe --help
```

The executable name differs on platforms that do not use `.exe`. Quote paths
and text containing spaces. Existing database and backup destinations are never
overwritten. The remaining examples assume the published executable is in the
current directory.

The database path is never hidden: `project init`, `sample create`, and
`ai-assistant-shell` use the path supplied by the caller, and successful project
results report the normalized path. Initialization and backup use an adjacent
unique `*.tmp` file only while producing the final atomic `.vw.db`; failed
cleanup remnants, SQLite journal/WAL sidecars, and test databases under the OS
temp directory are not canonical project files. `project backup` writes a
verified portable copy to the explicit destination, while `project export-sql`
writes deterministic text to stdout.

One-shot and NDJSON structured results go to stdout; the shell writes readable
status text there. Errors and unresolved-session warnings go to stderr. The
process exit codes are:

| Code | Meaning |
|---:|---|
| 0 | Success |
| 1 | Invalid command, JSON, or argument |
| 2 | Project, query, change, or validation error |
| 3 | Unexpected internal error |
| 4 | Broken input or output pipe |
| 130 | Cancellation |

## Discover the public surface

```powershell
./ValidatedWorld.Cli.exe --help
./ValidatedWorld.Cli.exe project --help
./ValidatedWorld.Cli.exe read --help
./ValidatedWorld.Cli.exe sample --help
./ValidatedWorld.Cli.exe shell --help
./ValidatedWorld.Cli.exe ndjson --help
```

Inside an NDJSON process, send `host.help` to obtain the complete command and
payload catalog for that executable version:

```json
{"version":1,"command":"host.help","payload":{}}
```

## Project and sample commands

Create a minimal project containing only its purpose node:

```powershell
./ValidatedWorld.Cli.exe project init world.vw.db world-id `
    "World title" purpose "The governing purpose of this world"
```

Inspect and protect a project:

```powershell
./ValidatedWorld.Cli.exe project status world.vw.db
./ValidatedWorld.Cli.exe project open world.vw.db
./ValidatedWorld.Cli.exe project verify world.vw.db
./ValidatedWorld.Cli.exe project backup world.vw.db world-backup.vw.db
./ValidatedWorld.Cli.exe project export-sql world.vw.db > world.sql
./ValidatedWorld.Cli.exe project diff world-before.vw.db world.vw.db --limit 100
```

List or create a built-in disposable sample:

```powershell
./ValidatedWorld.Cli.exe sample list
./ValidatedWorld.Cli.exe sample create technical-project sample.vw.db
```

`project open` returns the complete graph and can be large. Prefer bounded read
commands when only part of a project is needed.

## Semantic database diff

`project diff` compares two verified files belonging to the same project. It is
read-only and makes no AI call:

```powershell
./ValidatedWorld.Cli.exe project diff base.vw.db target.vw.db `
    --limit 100
./ValidatedWorld.Cli.exe project diff base.vw.db target.vw.db `
    --limit 100 --cursor <nextCursor>
```

The JSON result contains:

- `basePath`, `targetPath`, `projectId`, and both state fingerprints;
- `metadataChanges` for title or purpose-node changes;
- a `summary` of metadata and node/edge adds, replacements, and removals;
- bounded `items`, `totalCount`, `nextCursor`, and `omission` fields.

An added item contains its complete `newNode` or `newEdge`; a removed item
contains its complete `oldNode` or `oldEdge`. A replacement contains both and a
`changedFields` list. Nodes precede edges, with ordinal stable-ID ordering inside
each category. Summary and metadata remain on every page.

The cursor belongs to the exact project ID, base/target fingerprints, input
order, and page limit. Reversing the comparison, changing either database, or
changing `--limit` requires starting again without the old cursor. Identical
files succeed with no items. Different project IDs, invalid databases, and bad
cursors fail explicitly.

The NDJSON equivalent is:

```json
{"version":1,"command":"project.diff","payload":{"basePath":"base.vw.db","targetPath":"target.vw.db","limit":100}}
```

Use a `project backup` made before editing as the base, then diff it against the
result. A Git revision materialized as a `.vw.db` file is equally valid. Diff
output is not stored in either database.

### Repository review procedure

For a repository-backed project, treat a graph change and its matching source,
document, or content edits as one review unit. Back up the last accepted graph
outside the repository, make the reviewed graph change, and place its semantic
diff beside the ordinary source diff:

```powershell
./ValidatedWorld.Cli.exe project backup world.vw.db `
    $env:TEMP/world-before.vw.db
./ValidatedWorld.Cli.exe project diff $env:TEMP/world-before.vw.db `
    world.vw.db --limit 100
```

Continue `project diff` until `nextCursor` is null. The diff identifies exact
database changes; bounded `read search`, `read tag`, `read dependencies`,
`affected`, and `context` queries supply the surrounding meaning needed to
compare them with external artifacts. Review and merge both sides together.

The accepted, structurally verified database is then the trusted baseline for
the next delta; that trust is inherited from prior human or agent review, not
from a claim that validation proved its contents true. Semantic or design
changes require both graph and artifact changes. Meaningful artifact work also
normally carries a graph delta when its semantics were already planned: update
the relevant phase, status, or progress entities to record delivery. A graph may
describe future work ahead of implementation only when that boundary is
explicit and searchable.

Only corrective or non-semantic maintenance that changes neither intended
meaning nor recorded delivery state may omit the graph edit. The review should
state why the accepted graph already covers the work. Phase and status tags are
project-defined vocabulary, not hidden engine behavior, but their persisted
changes are still visible to semantic diff and bounded queries.

The graph's public data can also serve as input to independent tooling that
generates project artifacts.

## Bounded reads

```powershell
./ValidatedWorld.Cli.exe read node world.vw.db tamriel
./ValidatedWorld.Cli.exe read edge world.vw.db tamriel-scope-parent
./ValidatedWorld.Cli.exe read nodes world.vw.db --limit 100
./ValidatedWorld.Cli.exe read edges world.vw.db --limit 100
./ValidatedWorld.Cli.exe read search world.vw.db continent --limit 25
./ValidatedWorld.Cli.exe read ranked-search world.vw.db "golden claw" --limit 25
./ValidatedWorld.Cli.exe read tag world.vw.db quest:golden-claw --limit 25
./ValidatedWorld.Cli.exe read scope world.vw.db tamriel --limit 100 `
    --max-depth 1000 --max-nodes 10000
./ValidatedWorld.Cli.exe read neighbors world.vw.db tamriel --limit 100
./ValidatedWorld.Cli.exe read dependencies world.vw.db tamriel --limit 100
./ValidatedWorld.Cli.exe read path world.vw.db tamriel skyrim `
    --max-depth 1000 --max-nodes 10000
./ValidatedWorld.Cli.exe read context world.vw.db skyrim,high-hrothgar `
    --max-depth 1000 --max-nodes 10000
./ValidatedWorld.Cli.exe read health world.vw.db --limit 25
```

Paged results contain `nextCursor` and an explicit omission while more results
exist. Pass that exact token back with `--cursor`. Traversal bounds return
explicit omissions rather than silently reporting a complete result.

`neighbors` describes stored edge endpoints. `dependencies` describes expanded
review arcs. `path` follows review arcs, not the scope tree. `scope` returns the
selected node, its upstream scope, and paged descendants. `context` returns the
combined scope-upstream context for the requested node IDs without sibling
fan-out.

`health` (also available as `report`) returns a bounded deterministic graph
observability report. It includes scope coverage, nodes whose scope lineage
does not reach the purpose, review-arc fan-out sources, suspiciously isolated
non-structural nodes, semantic edges without rationale, tag frequencies, and
untagged node/edge counts. Each report section has its own `totalCount` and
`omittedCount`; these are author diagnostics and heuristics, not proof or
automatic dependency creation. The NDJSON commands are `read.health` and
`read.report` with payload `{path,limit?,expectedProjectId?}`.

`search` is case-insensitive substring discovery across node IDs/text/kinds/tags
and edge IDs/labels/rationales/tags. `tag` is an exact case-sensitive lookup
across node and edge tags. Both return the same bounded search-hit shape,
including the complete matching node or edge.

`ranked-search` is an additive lexical discovery query. It tokenizes the input,
recognizes quoted phrases (and an unquoted multi-token phrase), and ranks exact
stable-ID matches above exact case-sensitive tag matches, phrases, text tokens,
and metadata tokens. Metadata includes kinds, relationships, rationales, tags,
and attribute names and values. Results are deterministically ordered by score,
stable ID, and entity kind; every result includes `score` and `matches` with the
field, term, match kind, and score contribution that explain the ranking. Its
cursor is bound to the exact ranked query and project fingerprint. The NDJSON
equivalent is `read.ranked_search` with the same payload as `read.search`.

## Stateful shell

Open one project and keep the process running:

```powershell
./ValidatedWorld.Cli.exe shell world.vw.db
```

The shell uses ordinary flag-based commands, not JSON. It automatically selects
the purpose root when it opens, so `pwd` and `dir` are immediately useful. Type
`help`, `help navigation`, `help node`, `help edge`, or `help review` inside it.
It remembers the current project, selected node and edge, active change, latest
fingerprints, accumulated operations, affected analysis, and review state. A
command failure is printed to stderr and the shell remains usable.

Navigate without starting a change:

```text
status
pwd
dir --limit 20
cd geography
dir --depth 2 --upstream 2 --limit 40
cd ..
cd /
root
search --text "sixth continent" --limit 20
cd continent-count
node show
edge select --id roster-informs-atlas
edge show
```

The selected node acts like the shell's working directory. `cd ID` (or
`cd --id ID`) selects any stable node ID, `cd ..` selects its immediate scope
parent, and `cd /` or `root` returns to the purpose root. `pwd` prints the full
scope path as stable IDs, for example `/purpose/geography/continent-count`.
Paths are descriptive; because node IDs are globally unique, `cd` takes one ID
rather than requiring a repeated absolute path.

`dir` and its `ls` alias print the selected node (`[.]`), then nearby
connections. Scope parents are labeled `[..1]`, `[..2]`, and so on; scope
children and deeper descendants are labeled `[scope +1]`, `[scope +2]`, and so
on. Direct non-scope edges are shown in both stored endpoint directions as
`[out]` or `[in]`, with the stable edge ID, relationship, and review direction.
Thus a `both` review edge remains visibly different from merely displaying both
incoming and outgoing neighbors. `--depth N` bounds scope descendants,
`--upstream N` bounds ancestors, `--limit N` bounds all entries other than
`[.]`, and `--scope-only` omits semantic neighbors. Defaults are depth 1,
upstream 1, and limit 20; depth or upstream may be zero. An omission count says
when the limit hid additional connections.

Navigation always reads the current proposed graph. An uncommitted `node move`
therefore appears under its new parent immediately. `node list` and
`node select --id ID` remain available as flat discovery and explicit-selection
forms.

Begin one in-memory transaction, then make small incremental edits:

```text
begin --author "Morgan" --intent "Add Atmora and reconcile the continent roster"
cd continent-count
node set --text "The world has six recognized continents."
node add --id atmora --text "Atmora is the sixth recognized continent." --kind continent --parent geography
edge add --id atmora-member-of-roster --source atmora --target continent-roster --relationship member-of --direction source-to-target
```

Each mutating shell command patches the current proposal. It does not replace or
require resending the accumulated batch. Repeated edits to one entity collapse
to its final operation; returning an entity exactly to its base value removes
that pending operation. `node move --parent ID` replaces the selected node's
existing `scope-parent` target. `node remove` also includes all current incident
edges, reports how many it included, and selects the removed node's former scope
parent so navigation remains usable.

Single-value commands cover ordinary fields and metadata:

```text
node set --text "Replacement text"
node set --kind claim
node set --clear-kind
node tag-add --tag roster:continent
node tag-remove --tag roster:continent
node attribute-set --name count --type integer --value 6
node attribute-remove --name count

edge set --relationship informs
edge set --direction source-to-target
edge set --rationale "The atlas repeats the roster."
edge set --clear-rationale
edge tag-add --tag artifact:atlas
edge attribute-set --name confidence --type decimal --value 0.9
```

Attribute types are `text`, `integer`, `decimal`, `boolean`, `symbol`, and
`instant`; instants use the round-trip UTC `O` format. Supply `--id ID` to edit
an entity without selecting it first.

Inspect and review the growing transaction in small steps:

```text
changes
affected
review --id continent-count --as updated
review --id atlas-summary --as reviewed-no-change
review --id obsolete-note --as not-applicable --rationale "No longer describes this roster"
context mark --id purpose
context mark --id geography
validate
```

Finish with a one-line commit or discard:

```text
commit
commit --bypass-ai-review
discard
exit
```

The bypass applies only to that `commit`. It does not change configuration and
does not bypass structural validation, affected-node review, context coverage,
fingerprints, stale detection, or SQLite atomicity. A block from the semantic
reviewer is formatted as readable text with its cited concerns.

## Graph rules needed by CLI authors

- Node and edge IDs share one case-sensitive stable-ID namespace.
- Exactly one node is the purpose. It has no scope parent.
- Every other node has exactly one outgoing `scope-parent` edge to its parent.
- `scope-parent` edges always use review direction `none`.
- Ordinary edge labels do not imply propagation. Set `reviewDirection`
  explicitly to `none`, `sourceToTarget`, `targetToSource`, or `both`.
- Changing an ordinary node seeds recursive review-arc traversal. Changing a
  scope node also selects its current and proposed scope descendants. Scope
  ancestors are context only unless independently affected or directly edited.
- Adding or replacing an edge uses the union of its current and proposed review
  arcs, so removing or redirecting a relationship cannot hide old consequences.
- A changed `scope-parent` is special despite its `none` review direction. Its
  old and new child subtrees and immediate parents require review, and both
  ancestry lineages are context. The parents do not fan out through siblings.

Nodes and edges may use any `kind`, tags, and scalar attributes. The common
engine does not give those values hidden semantics. A meaningful connection must
be represented as an edge if it is expected to affect review selection.

### Tags and external views

Tags are useful when a system outside ValidatedWorld needs a stable secondary
index. Prefer short namespaced labels so ownership and intent remain obvious:

```text
quest:golden-claw
runtime:content
enable:lucan-dead
region:whiterun
```

An external game build tool could use exact tag lookup to collect every node
marked `enable:lucan-dead`, then compile that already-authored content into its
own runtime representation. ValidatedWorld does not toggle nodes, execute the
condition, or participate during gameplay. This keeps shipped runtime state
separate from design-time review while avoiding condition syntax hidden inside
ordinary prose.

Keep these boundaries when designing tag conventions:

- A tag answers “which labeled set contains this entity?” It is unordered and
  carries no value beyond exact membership.
- Use a scalar attribute for named data such as `chapter = 3` or
  `runtime-key = lucan-status` when equality to a value is the important fact.
- Use an explicit directed edge when changing one entity can make another
  stale. Shared tags do not form a dependency clique and are not traversed by
  affected analysis.
- Exact tag lookup may narrow a working view or find candidate entities. It
  must not filter an affected preview, required scope context, or review
  obligations after a change has been proposed.
- Tags on affected nodes and edges are present in the structured result, so an
  integration may group or annotate the review without losing graph evidence.
- Changing tags means replacing the node or edge through the normal reviewed
  transaction. Treat tag names and casing as a small external API once another
  system consumes them.

This provides practical runtime organization without claiming that arbitrary
tag combinations are statically valid gameplay states. If a product later
needs rules such as mutual exclusion, transition legality, or scenario
coverage, those are explicit validation-profile concerns rather than implicit
tag behavior in the common engine.

## Modeling graphs that age well

Treat the scope tree as stable containment and review context, not as automatic
semantic inference. In particular, adding a child does not make every sibling a
dependency and the engine does not derive counts from children.

Useful defaults for a human or authoring agent are:

- Keep scope-container text broad and stable. Put frequently changing names,
  counts, lists, dates, and conclusions in separate claim nodes below or beside
  the scope.
- Prefer one important claim per node. Stable IDs should describe identity, not
  repeat mutable display names.
- Direct edges from the source of truth toward the nodes that may become stale.
  `sourceToTarget` is the common choice. Use `both` only when either endpoint
  genuinely requires the other to be reconsidered.
- Represent a closed set through a roster or aggregate claim. Point each member
  toward that claim, then point the claim toward summaries, dialogue, tests, or
  artifact anchors that repeat it. Members do not need to be mutually linked.
- Link canonical name or terminology claims to the chunks that repeat them.
  Prefer useful document, quest, scene, or dialogue anchors over an edge for
  every word occurrence.
- Search for exact counts, lists, names, and words such as “all”, “only”,
  “every”, and “none” before changing a modeled concept. Search results are
  candidate relationships, not automatically trusted dependencies.
- Preview the affected set before review. An unexpectedly tiny set can indicate
  a missing relationship; an unexpectedly huge set can indicate a volatile fact
  stored in a scope node or an edge directed too broadly.

For example, model a world and its continent roster as:

```text
purpose
└─ world scope
   ├─ geography scope
   │  ├─ continent A scope ──member-of-roster──▶ roster claim
   │  ├─ continent B scope ──member-of-roster──▶ roster claim
   │  └─ continent C scope ──member-of-roster──▶ roster claim
   └─ roster claim ──informs──▶ atlas/dialogue/artifact anchors
```

Adding a member with its roster edge selects the new member, the roster, and
the roster's consumers without selecting every existing member. A local fact
under one continent remains local unless explicit semantic edges say otherwise.
Changing the whole continent scope intentionally selects its descendants.

## NDJSON framing and sessions

Start one host and keep its stdin and stdout open:

```powershell
./ValidatedWorld.Cli.exe ndjson
```

Every input line is one strict JSON request:

```json
{"version":1,"command":"project.status","payload":{"path":"world.vw.db"}}
```

Every output line has `version`, `command`, `status`, and `payload`. An error is
returned as an error payload; the host then continues reading later lines.
Unknown fields and protocol versions are rejected.

One process may hold one active session per project. EOF, cancellation, or
`host.exit` ends the process. Any unresolved session is lost and produces a
stderr warning. No operation or review state is written to SQLite until
`change.write` succeeds.

NDJSON intentionally remains an explicit request/result protocol. Mutating
commands require the complete latest `reference` so asynchronous or external
clients cannot accidentally act on stale state. The reference is a small bundle
of opaque fingerprints, not graph content, and every successful mutation
returns the next reference.

`change.patch` is the normal incremental path for an interactive client or
authoring agent. Its `operations` contains only the entities being changed in
that request; the host merges them into the session's normalized pending batch.
`change.apply` remains available when a client deliberately wants to replace the
complete pending batch. Both commands recalculate projection, validation,
affected analysis, review invalidation, counts, and fingerprints.

Change-session responses retain their original complete form by default for
protocol compatibility. Set `includeOperations:false` and
`includeProposedGraph:false` on `change.begin`, `change.show`, `change.apply`,
`change.patch`, `change.expand`, `change.review`, or `change.validate` when the
client does not need those large fields. The response still includes the exact
reference, operation/node/edge counts, affected evidence, review state, and
readiness. For a final proposal preview, use `change.show` with
`includeOperations:true` and `includeProposedGraph:false` to retrieve the
normalized operation batch without retrieving the whole graph.

## NDJSON project initialization

`project.init` creates only project metadata and one purpose node. It does not
accept a complete graph and it does not create a populated project outside the
ordinary change-session review workflow. Add every later node and edge through
`change.begin`, `change.patch` or `change.apply`, `change.review`, and
`change.write`.

The request below is formatted for reading; serialize it as one physical line
before sending it to the NDJSON host:

```json
{
  "version": 1,
  "command": "project.init",
  "payload": {
    "path": "world.vw.db",
    "projectId": "world-id",
    "title": "World title",
    "purposeNodeId": "purpose",
    "purposeText": "A coherent game world"
  }
}
```

## Manual change workflow

The normal sequence is:

```text
change.begin
→ change.focus (optional helper for new-node scope parents)
→ change.patch (repeat with one or a few entity operations)
→ inspect change.affected / change.validate
→ change.review
→ change.write or change.discard
→ host.exit
```

Begin a session:

```json
{
  "version": 1,
  "command": "change.begin",
  "payload": {
    "path": "world.vw.db",
    "projectId": "world-id",
    "author": "operator",
    "intent": "Add a sixth continent",
    "includeOperations": false,
    "includeProposedGraph": false
  }
}
```

The response contains `payload.reference`. Copy that entire object into the next
mutating command. Do not reconstruct it or keep using an earlier reference.
Every proposal or review change returns a new exact reference and makes the old
one stale.

An operation batch has one final operation per entity ID. Add and replace carry
the complete node or edge; remove carries only its entity kind and ID:

```json
{
  "operations": [
    {
      "kind": "add",
      "entityKind": "node",
      "entityId": "continent-6",
      "node": {
        "id": "continent-6",
        "text": "Atmora is recognized as the sixth continent",
        "kind": "continent",
        "tags": [],
        "attributes": []
      },
      "edge": null
    }
  ]
}
```

For new nodes, `change.focus` can add only the explicit scope-parent edges you
request:

```json
{
  "version": 1,
  "command": "change.focus",
  "payload": {
    "reference": {
      "projectId": "...",
      "sessionId": "...",
      "baseFingerprint": "...",
      "operationFingerprint": "...",
      "proposedFingerprint": "...",
      "affectedFingerprint": "...",
      "reviewFingerprint": "..."
    },
    "operations": {
      "operations": [
        {
          "kind": "add",
          "entityKind": "node",
          "entityId": "continent-6",
          "node": {
            "id": "continent-6",
            "text": "Atmora is recognized as the sixth continent",
            "kind": "continent",
            "tags": [],
            "attributes": []
          },
          "edge": null
        }
      ]
    },
    "scopeParents": [
      {
        "childId": "continent-6",
        "parentId": "tamriel",
        "edgeId": "continent-6-scope-parent"
      }
    ]
  }
}
```

Use the returned `expandedOperations` in `change.patch`. A patch carries only
the new operations; do not resend operations already accumulated in the
session. Replacing a pending addition keeps it as one final `add`, removing a
pending addition cancels it, and replacing a base entity with its exact base
value removes that pending operation. Use only the latest returned reference;
an older reference is rejected as stale.

Inspect the resulting
`affected.affectedNodes`, explanation paths, `edgeChanges`, `scopeContext`, both
validation results, and any omissions. Directly changed nodes normally receive
`updated`; every other affected node begins `pending`. Context-only nodes need
presentation coverage but no disposition.

Omissions are returned as compact groups rather than one response item per
omitted traversal candidate. Each group contains a reason, total count, a
small sample, and a fingerprint. Request the exact omitted candidates with the
latest change reference:

```json
{
  "version": 1,
  "command": "change.omission-details",
  "payload": {
    "reference": { "projectId": "...", "sessionId": "...", "baseFingerprint": "...",
      "operationFingerprint": "...", "proposedFingerprint": "...",
      "affectedFingerprint": "...", "reviewFingerprint": "..." },
    "fingerprint": "<detailsFingerprint>",
    "limit": 100
  }
}
```

The returned `nextCursor` is bound to the supplied detail fingerprint and is
opaque. A changed proposal or a cursor from another group is rejected.

A review request supplies all current affected-node dispositions and every
presented context-node ID:

```json
{
  "version": 1,
  "command": "change.review",
  "payload": {
    "reference": {
      "projectId": "...",
      "sessionId": "...",
      "baseFingerprint": "...",
      "operationFingerprint": "...",
      "proposedFingerprint": "...",
      "affectedFingerprint": "...",
      "reviewFingerprint": "..."
    },
    "dispositions": [
      { "nodeId": "continent-6", "kind": "updated" },
      { "nodeId": "continent-count", "kind": "updated" },
      { "nodeId": "travel-guide", "kind": "reviewedNoChange" }
    ],
    "presentedContextNodeIds": ["purpose", "tamriel"]
  }
}
```

Disposition kinds are `updated`, `reviewedNoChange`, `notApplicable`, and
`pending`. `notApplicable` requires a rationale. `change.validate` reports
whether the exact current proposal is ready. `change.write` rechecks everything
inside one SQLite transaction and either commits the complete graph or leaves
the previous state unchanged.

Finish explicitly:

```json
{"version":1,"command":"host.exit","payload":{}}
```

Use `change.show` or `change.affected` with
`{"session":{"projectId":"...","sessionId":"..."}}` for non-mutating session
inspection. Use the latest complete reference for `change.expand`,
`change.validate`, `change.write`, or `change.discard`.

## Conversational AI authoring

The conversational authoring entry point is:

```powershell
dotnet run --project src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj -- ai-assistant-shell project.vw.db
```

Describe the project or desired change in English. The authoring agent checks
status, performs bounded searches and reads, and builds one incremental
in-memory proposal with strict node/edge tools. It cannot execute SQL, write the
database directly, record review dispositions, or bypass independent semantic
review. Type `discard` to abandon its current proposal or `exit` to leave.

When the agent believes the proposal is ready, the application—not the
model—prints every operation, affected path, required scope context, and exact
fingerprint. The prompt accepts only `yes` as approval. That response records
the displayed affected/context review and creates a ten-minute process-local
approval for the exact current reference. Any edit invalidates it. The normal
write still invokes the configured independent semantic reviewer.

If AI authoring is disabled or has no configured key, this command opens an
existing database in the manual shell. Use `ai-assistant-shell --help` for the
short command reference and the root README for the explicit configuration
defaults.

## Optional semantic AI write gate

The root README documents one-time OpenAI configuration. `ai.status` inspects
the effective policy without making a provider call:

```json
{"version":1,"command":"ai.status","payload":{}}
```

There is no separate paid-review command. After the normal manual review is
ready, `change.write` automatically invokes semantic review when the key is
configured and `AiReview:Enabled=true`:

```json
{
  "version": 1,
  "command": "change.write",
  "payload": {
    "reference": {
      "projectId": "...",
      "sessionId": "...",
      "baseFingerprint": "...",
      "operationFingerprint": "...",
      "proposedFingerprint": "...",
      "affectedFingerprint": "...",
      "reviewFingerprint": "..."
    },
    "bypassAiReview": false
  }
}
```

The provider receives the exact proposed operation/affected/context slice and
returns a strict `allow` or `block` decision with cited feedback. Only `allow`
permits SQLite to open the write transaction. `block`, refusal, timeout,
malformed output, or provider failure leaves the database unchanged. A current
decision is cached against all proposal/review fingerprints, so retrying the
unchanged write does not make another paid call; changing the session invalidates
it.

Before dispatch, the application measures the serialized request in bytes,
estimated input tokens, and bounded component-item counts. The configured
ceilings are `AiReview:MaxRequestBytes` (default `1000000`),
`AiReview:MaxRequestItems` (default `20000`), and
`AiReview:MaxRequestTokens` (default `250000`). An over-budget request is
reported as inconclusive with component counts and guidance to split or
remodel the change, or use the explicit manual bypass. The application never
partitions one write or makes multiple paid review calls.

To use the manual-only path for one write, set `bypassAiReview` to `true`. The
result records the bypass. It skips only the provider gate: structural
validation, affected-node dispositions, context coverage, stale-reference
checks, and atomic-write safeguards still apply. Omitting the field is equivalent
to `false`.
