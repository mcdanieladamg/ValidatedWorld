# ValidatedWorld CLI usage

ValidatedWorld is currently a local, headless .NET 10 command-line application.
One-shot commands cover project storage and bounded reads. A long-lived NDJSON
process is required for change sessions because unfinished operations and review
state exist only in memory.

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

The executable name differs on platforms that do not use `.exe`. The release
evidence currently covers Windows x64 only. Quote paths and text containing
spaces. Existing database and backup destinations are never overwritten. The
remaining examples assume the published executable is in the current directory.

Structured command results go to stdout. Errors and unresolved-session warnings
go to stderr. The process exit codes are:

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
```

List or create a built-in disposable sample:

```powershell
./ValidatedWorld.Cli.exe sample list
./ValidatedWorld.Cli.exe sample create technical-project sample.vw.db
```

`project open` returns the complete graph and can be large. Prefer bounded read
commands when only part of a project is needed.

## Bounded reads

```powershell
./ValidatedWorld.Cli.exe read node world.vw.db tamriel
./ValidatedWorld.Cli.exe read edge world.vw.db tamriel-scope-parent
./ValidatedWorld.Cli.exe read nodes world.vw.db --limit 100
./ValidatedWorld.Cli.exe read edges world.vw.db --limit 100
./ValidatedWorld.Cli.exe read search world.vw.db continent --limit 25
./ValidatedWorld.Cli.exe read tag world.vw.db quest:golden-claw --limit 25
./ValidatedWorld.Cli.exe read scope world.vw.db tamriel --limit 100 `
    --max-depth 1000 --max-nodes 10000
./ValidatedWorld.Cli.exe read neighbors world.vw.db tamriel --limit 100
./ValidatedWorld.Cli.exe read dependencies world.vw.db tamriel --limit 100
./ValidatedWorld.Cli.exe read path world.vw.db tamriel skyrim `
    --max-depth 1000 --max-nodes 10000
./ValidatedWorld.Cli.exe read context world.vw.db skyrim,high-hrothgar `
    --max-depth 1000 --max-nodes 10000
```

Paged results contain `nextCursor` and an explicit omission while more results
exist. Pass that exact token back with `--cursor`. Traversal bounds return
explicit omissions rather than silently reporting a complete result.

`neighbors` describes stored edge endpoints. `dependencies` describes expanded
review arcs. `path` follows review arcs, not the scope tree. `scope` returns the
selected node, its upstream scope, and paged descendants. `context` returns the
combined scope-upstream context for the requested node IDs without sibling
fan-out.

`search` is case-insensitive substring discovery across node IDs/text/kinds/tags
and edge IDs/labels/rationales/tags. `tag` is an exact case-sensitive lookup
across node and edge tags. Both return the same bounded search-hit shape,
including the complete matching node or edge.

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

The [lore modeling study](lore_modeling_study.md) records a public-CLI test of
this pattern, alternatives, migration behavior, and the scope-topology rule.

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

## Create a complete graph through NDJSON

`project.init` accepts the complete graph DTO. The examples below are formatted
for reading; serialize each request as one physical line before sending it to
the NDJSON host. This small graph contains a purpose, one child, and the child's
required scope edge:

```json
{
  "version": 1,
  "command": "project.init",
  "payload": {
    "path": "world.vw.db",
    "graph": {
      "projectId": "world-id",
      "title": "World title",
      "purposeNodeId": "purpose",
      "nodes": [
        {
          "id": "purpose",
          "text": "A coherent game world",
          "kind": "purpose",
          "tags": [],
          "attributes": []
        },
        {
          "id": "tamriel",
          "text": "Tamriel is the primary known world region",
          "kind": "scope",
          "tags": [],
          "attributes": []
        }
      ],
      "edges": [
        {
          "id": "tamriel-scope-parent",
          "source": "tamriel",
          "target": "purpose",
          "relationship": "scope-parent",
          "reviewDirection": "none",
          "rationale": null,
          "tags": [],
          "attributes": []
        }
      ]
    }
  }
}
```

For a large initial import, generate one graph object deliberately and validate
the returned project. For incremental authoring, create a minimal project and
use a change session.

## Manual change workflow

The normal sequence is:

```text
change.begin
→ change.focus (optional helper for new-node scope parents)
→ change.apply
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
    "intent": "Add a sixth continent"
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

Use the returned `expandedOperations` in `change.apply`. Inspect the resulting
`affected.affectedNodes`, explanation paths, `edgeChanges`, `scopeContext`, both
validation results, and any omissions. Directly changed nodes normally receive
`updated`; every other affected node begins `pending`. Context-only nodes need
presentation coverage but no disposition.

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

## Optional semantic AI review

The root README documents one-time OpenAI configuration. With or without a key,
inspect runtime availability inside the NDJSON process without making a provider
call:

```json
{"version":1,"command":"ai.status","payload":{}}
```

After `change.apply` or `change.expand`, copy the entire latest reference into
`ai.review`. A false authorization is an offline fallback check and never calls
the provider:

```json
{
  "version": 1,
  "command": "ai.review",
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
    "authorizeProviderCall": false
  }
}
```

Set `authorizeProviderCall` to `true` only when intentionally authorizing one
paid review of that exact affected fingerprint. The request sends the complete
proposed operation/affected/context slice to OpenAI, uses no tools, and performs
no automatic paid retry. Polling continues only the response created by that
single call. The result includes status, cited concerns, usage, duration, a
request fingerprint, and its exact session binding.

AI concerns are advisory and exist only in the in-memory session. They never
change dispositions, make an invalid proposal writable, write SQLite, or replace
the manual `change.review` workflow. If the proposal changes, the prior result
is reported as stale. A disabled, unconfigured, refused, timed-out, or malformed
provider result leaves the complete manual workflow available.
