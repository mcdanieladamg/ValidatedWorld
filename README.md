# ValidatedWorld

ValidatedWorld is an experimental **semantic change-control engine for complex
project data**.

The idea is that virtually anything—a novel, technical design, patent outline,
game lore, or campaign—can be represented as a dependency graph. Conclusions
depend on assumptions and evidence; scenes depend on prior events and character
knowledge; requirements depend on definitions and are realized by decisions and
tests.

ValidatedWorld stores that explicit dependency graph in an embedded SQLite
project file. It validates proposed transactions, calculates their affected
subgraph, requires every selected affected node to be reviewed, and commits the
complete new state in a transaction as an all-or-nothing change, which is
considered to be a validated state.

The graph is primarily made of human-readable text nodes and labeled
relationships, not necessarily a complex type system. Optional profiles may add
structured properties, vocabulary, and deterministic checks for particular
uses, but they are aids rather than a prerequisite for an ordinary project.
Whether the text still makes sense across the affected relationships must be
judged semantically by a thinking participant—the user or an AI. The graph
should remain simple in theory while potentially expansive in scope.

## The project thesis is always upstream

Every project begins with one root node: its overall thesis, purpose, scope, or
governing statement. Every other node belongs to the scope tree through exactly
one `scope-parent`. Following those parent edges from any node back to the root
defines that node's unique **scope-upstream path**. In this precise sense, the
root is a transitive scope dependency of every non-root node and therefore of
every change made beneath it. Nodes may also have arbitrary explicit semantic
relationships. Following those relationships selects additional affected nodes;
each newly affected node contributes its own unique scope-upstream path. The
result can therefore contain several scope lineages alongside downstream,
upstream, or lateral semantic paths selected by the labeled edges.

For every proposed change, ValidatedWorld walks that path upward from every
changed or affected node and includes every node on it—including the project
root—as mandatory semantic review context. The change must remain consistent
with its immediate scope, every containing scope, and ultimately the project's
thesis. Those upstream nodes normally do not change; they are present so the
human or AI can inspect the proposal against them.

Upstream context is not read-only. If that inspection reveals that an ancestor
also needs to change, the user or agent may edit it in the same change session.
It then becomes a direct change seed and ValidatedWorld recalculates from it,
which may expand the affected set substantially. Editing the project root is the
intentional case that can make the transaction project-wide.

Including an upstream node does not make it a propagation seed and does not cause
the app to walk back down through its other children. A local change therefore
does not pull in unrelated sibling branches merely because their paths share the
root. Only directly changing a scope node selects its descendants. This gives
every change its natural connection to the project thesis without turning every
change into a whole-project review.

All changes are authored as one in-memory change session: a batch that moves the
project from one reviewed, structurally valid state to another. As the user or
agent edits nodes and edges, ValidatedWorld recalculates and presents every
explicitly affected dependency and dependent selected by those relationships.
That set may extend downward, upward, laterally, or in both directions. Editing
or deleting another affected node may expand the set again. Commit is enabled
only after the complete current affected set has been examined and every
structural check passes. When semantic AI review is configured and enabled, the
write attempt also requires its `allow` decision for that exact proposal;
SQLite then applies the batch atomically.

ValidatedWorld is intended to offer an **AI-first, headless** experience, but AI
is always optional rather than a requirement for using the application. The
initial product is therefore designed around text-based commands and structured
results, with no substantial graphical front end planned. A human can operate
the same change-and-review workflow directly.

When AI authoring is enabled, the user describes what they want and the built-in
authoring agent uses bounded search and transaction tools to build or change the
graph. Keeping that agent inside the application lets ValidatedWorld standardize
its prompts and tool contract. The agent asks questions when meaning is genuinely
ambiguous or when following the affected relationships exposes a non-trivial
choice. Graph design and relationship direction keep the relevant working set as
small as the modeled semantics allow, so neither the user nor the AI must absorb
an entire large project for a local change - unless it is a change on the thesis.

## Storage and protocol

The authoritative workspace is one portable SQLite application file:

```text
project.vw.db
```

That file is a user's mutable project state, not a source-controlled project
template. The repository ignores `.vw.db` files and their SQLite sidecars
outside `tests/`. Samples contain reviewed source descriptions, change scripts,
and expected results; the app generates disposable databases locally. A binary
database belongs in `tests/` only when a deliberately constructed persistence or
corruption fixture cannot reasonably be generated during the test.

SQLite supplies atomic transactions, foreign keys, indexes, and efficient
queries. ValidatedWorld supplies the behavior a database schema cannot:
relationship-specific review direction, affected-subgraph expansion over the
current and proposed graph, explainable review obligations, optional profile
checks, and explicit valid/invalid/inconclusive outcomes.

The SQLite database is the sole complete representation and interchange format
for a project graph. Other tools may inspect the documented read-only schema and
views in a `.vw.db` file, or consume an application-produced SQLite backup or SQL
export.

## The simple mental model

Canonical project content is one graph:

- every authored fact, claim, requirement, character, event, constraint, scope,
  artifact anchor, or other concept is a stable-ID **node** whose primary content
  is human-readable text;
- every graph-relevant connection is a stable-ID **edge** with a source, target,
  human-readable relationship label, and declared review direction;
- exactly one node is the project-purpose root;
- every other node has exactly one `scope-parent` edge, forming a spanning tree;
- all remaining edges form a directed semantic multigraph. An edge may select
  review source-to-target, target-to-source, both ways, or not at all.

The application does not provide project-history versioning. It guarantees only
the current SQLite state. Uncommitted changes live in the running application
and are expected to be resolved or discarded before it closes. A fully reviewed
change set commits in one short SQLite transaction. Any stale-state, busy, I/O,
constraint, or mapping failure rolls back the whole attempt and leaves the prior
graph unchanged so the operation can be reviewed or retried.

Technical claims, fictional events, character knowledge, and game transitions
are example optional profiles over the same node/edge model.

## Modeling graphs that age well

Use the scope tree for stable containment: broad world, subsystem, chapter, or
artifact boundaries whose meaning should legitimately reach their descendants.
Keep volatile names, exact counts, complete lists, dates, and conclusions in
smaller claim nodes. Stable IDs should identify the enduring concept rather than
repeat mutable display text.

Direct semantic edges from a source of truth toward material that may become
stale. `sourceToTarget` is the usual direction; reserve `both` for genuine mutual
reconsideration. For a closed collection, point each member toward one roster or
aggregate claim, then point that claim toward summaries, dialogue, tests, or
artifact anchors that repeat it. This fan-in/fan-out pattern avoids a clique
between every member while still exposing changes to counts and enumerations.

Search for aliases and closed-world wording such as exact numbers, “all”,
“only”, and “every” when authoring changes. The common engine does not infer
those meanings from prose. Treat search hits as candidate dependencies, link at
a useful review unit rather than every word occurrence, and inspect every
affected preview: a surprisingly small set often signals a missing edge, while
a surprisingly large set may signal an unstable claim stored too high in scope
or an over-broad review direction.

Use tags for an additional, explicitly non-semantic organization layer. A tag
is an exact, case-sensitive label on a node or edge; bounded exact-tag lookup is
separate from broad text search. Namespaced labels such as `quest:golden-claw`,
`runtime:content`, or `enable:lucan-dead` can let an external tool assemble a
view or prevalidated content bundle without putting control syntax in prose.
Tags are returned with graph entities and affected previews, and changing tags
is a direct change to that entity. Shared tags never create review dependencies,
alter scope, or narrow required review context. If one fact can make another
stale, model that relationship with an explicit directed edge; if a scalar value
has a defined name, prefer an attribute. The external system owns any runtime
meaning assigned to tags—ValidatedWorld stores, searches, reviews, and exports
them but does not execute them as conditions.

A `scope-parent` change is itself meaningful topology. Adding, removing, or
redirecting one selects the old and new child subtrees and makes the old and new
immediate parents review obligations; both ancestry lineages are included
without pulling unrelated siblings. The [CLI usage guide](docs/cli_usage.md)
gives concrete patterns for humans and authoring agents.

## Product boundary

ValidatedWorld owns:

- the authoritative current `project.vw.db` state;
- stable node/edge IDs, text, labels, endpoints, and review direction;
- structural validation and optional profile validation;
- explained affected-subgraph expansion and complete review obligations;
- atomic in-memory-to-SQLite change transactions;
- a stateful flag-based shell that selects the purpose root and navigates the
  scope tree with `pwd`, `dir`/`ls`, `cd`, and `root`; and
- bounded structured commands for AIs, scripts, and integrations.

ValidatedWorld does **not** own the finished novel, paper, patent application,
manual, source tree, game project, or media. External artifact/anchor nodes may
point to those products, but the engine does not rewrite, render, publish, or
certify them.

The operation batch is the direct description of a pending change. There is no
separate semantic-diff format and no retained commit-history subsystem.

The planned AI support has two separate roles. The optional authoring agent
searches the project and operates the same change tools available to a human.
The optional semantic reviewer independently examines one complete proposed
transaction and its affected context when a database write is attempted. The
reviewer never edits the graph, but its strict `allow` or `block` decision is an
authorization gate for that exact write. The author never supplies or overrides
its own independent-review decision, and model judgment is not represented as
deterministic proof. Each role can be disabled. If no API key is configured,
both are skipped automatically and the complete manual workflow remains
available. In that mode, the human authors the change and personally reviews the
affected subgraph before commit.

## AI-first authoring direction

The intended AI-assisted flow is:

```text
user describes a new project or desired alteration
→ authoring agent searches/reads the graph or interprets supplied text
→ agent asks focused questions and creates explicit in-memory node/edge changes
→ structural checks and affected-subgraph expansion run
→ app shows the exact final operation/affected-set/state-fingerprint preview
→ user approves that exact proposal in the conversation
→ authoring agent calls the guarded commit tool
→ enabled independent semantic AI review automatically allows or blocks that write
→ a block returns cited feedback for repair/discussion and requires a new proposal
→ one atomic SQLite transaction succeeds or rolls back safely
```

The authoring agent's commit tool succeeds only after the user has approved the
exact current preview in ordinary conversation. A changed proposal or database
invalidates that approval.

This is the central product promise: the AI can safely and meaningfully work on
a project far larger than one context window. It should resemble asking the lore
master for a very large game whether a proposed addition can become canon, while
providing the same consistency-management value for non-game projects. The full
state persists in SQLite; the AI repeatedly searches and retrieves the relevant
working set while graph traversal and structural checks operate over the
authoritative graph. No step requires loading a WoW-sized world into one prompt.

If one affected set becomes unmanageably large, that may indicate an overly
connected graph or a genuinely project-wide change. Coordinating multiple agents
over partitioned review sets may be investigated later, but it is outside the
initial application scope.

Optional profiles may eventually make recurring domains easier to author and
check. The initial implementation must first prove that the plain text-node and
relationship model is useful without requiring a profile. An AI may use an
installed profile when one fits; unsupported structure remains plain graph data
unless the user later chooses to preserve a separately designed profile.

## Intended uses

- Technical work: definitions, assumptions, requirements, evidence, decisions,
  conclusions, implementations, verification, and traceability.
- Patent or standards planning: a structured claim/definition/evidence outline
  without claims of legal or scientific correctness.
- Novels and mysteries: canon facts, chronology, character knowledge, clues, and
  disclosure while manuscript text remains external.
- Games and campaigns: lore, design constraints, and—only through a future
  bounded profile—static transition specifications from which runtime states may
  be analyzed.

Despite the name, a “world” is any universe of connected nodes. Fiction is one
possible use, not the common engine's only purpose.

The [CLI usage guide](docs/cli_usage.md) documents both the stateful shell and
the alternative NDJSON interface protocol.
Technical requirements and the one-task-at-a-time implementation checklist are
in the [development plan](docs/development_plan.md).

## Optional OpenAI configuration

The manual workflow needs no API key. Both product AI features default on, but
either becomes active only when a key is configured. Paid live development
tests default off and never follow from merely configuring a key.

| Setting | Default | Effect |
|---|---:|---|
| `AiReview:Enabled` | `true` | Automatically run independent semantic review before a normal write when a key is configured. |
| `AiAuthoring:Enabled` | `true` | Make `ai-assistant-shell` use conversational OpenAI authoring when a key is configured. |
| `AiReview:LiveTests` | `false` | Opt into the paid T12 development test and credential-free request/response artifacts. |
| `AiAuthoring:LiveTests` | `false` | Opt into the paid T13 development tests and credential-free request/response artifacts. |

With no configured key, both AI roles are inactive and the complete manual
workflow remains available. Set either `Enabled` value to `false` to turn that
role off even while retaining the key. Environment variables use the `VW_`
prefix and replace `:` with `__`, for example
`VW_AIAUTHORING__ENABLED=false` or `VW_AIREVIEW__ENABLED=false`.

For a source checkout, store the key once in the existing .NET User Secrets
store. It stays outside the repository and persists across terminals:

```powershell
dotnet user-secrets set "AiReview:OpenAI:ApiKey" "<key>" --project src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj
```

That one key is shared by both initial AI roles. `AiAuthoring:OpenAI:ApiKey`
may be set separately when desired; authoring otherwise falls back to the
review key and then to `OPENAI_API_KEY`.

Developers and coding agents should verify effective configuration without
displaying the secret. Do not rely on checking `OPENAI_API_KEY` alone because a
key in .NET User Secrets is intentionally absent from that process variable:

```powershell
'{"version":1,"command":"ai.status","payload":{}}' | dotnet run --no-restore --project src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj -- ndjson
```

The returned payload says whether review is `configured` and `enabled` but
never includes the key. Do not use `dotnet user-secrets list` for this check.

Enable credential-free request/response capture only while intentionally
opting the corresponding paid development test into the normal test suite:

```powershell
dotnet user-secrets set "AiReview:LiveTests" "true" --project src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj
dotnet user-secrets set "AiAuthoring:LiveTests" "true" --project src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj
```

There is only one test command. It discovers the offline tests and both live
test classes together:

```powershell
dotnet test ValidatedWorld.slnx --no-build --no-restore
```

Each live test makes provider calls only when its own `LiveTests` setting is
`true` and that feature is enabled with an effective key. Otherwise its test
body returns without a provider call (the current test runner reports that case
as passed rather than skipped). Never override these settings in the test
command: the effective .NET configuration, including User Secrets, is the sole
gate. When enabled, the checks write credential-free `ai-review-live-*.json`
and `ai-authoring-live-*.json` files under `artifacts/` relative to the working
directory. These ignored development artifacts can contain project text and
model feedback. Sandbox network permission is handled as documented in
`AGENTS.md`.

Configuration uses ordinary .NET keys. The standard `OPENAI_API_KEY` variable
is also accepted. Both roles default to `Enabled=true`, `Provider=openai`,
`Model=gpt-5.6-terra`, and `TimeoutSeconds=1200`; both `LiveTests` values default
to `false`. Never commit, log, or paste an API key into project data.

Start the normal AI-first interface with:

```powershell
dotnet run --project src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj -- ai-assistant-shell project.vw.db
```

The authoring agent uses bounded search, read, incremental node/edge, preview,
approval, write, and discard tools. It has no raw SQL, direct write, automatic
review-disposition, or AI-review bypass tool. Before a write, the shell itself
shows the complete operations, affected evidence, required scope context, and
fingerprints. Only the human's exact `yes` records review coverage and creates a
short-lived approval bound to that exact state. Any subsequent proposal or
review change invalidates it. A configured independent semantic reviewer then
still gates the normal write.

If authoring is disabled or unconfigured, `ai-assistant-shell` opens an existing
database in the complete manual shell instead. The original `shell` and strict
`ndjson` surfaces remain available; AI authoring does not duplicate their
command vocabulary for humans.

A public/local OpenAI plugin is not the MVP interface. A plugin can standardize
tool discovery through MCP, but plugin-private storage is the wrong home for a
portable user-owned `.vw.db`, and a generic MCP tool call does not itself prove
that the human reviewed this exact proposal. The built-in assistant shell keeps
the database at the user-selected path, the in-memory session and approval in
one trusted local process, and the Application guarantees intact. A future
plugin may wrap a distributable local ValidatedWorld executable and reuse these
bounded tools; it must not move project ownership into an online service or
weaken exact approval.

When both the key and `AiReview:Enabled=true` are present, attempting
`change.write` authorizes one review of the exact current proposal. The call
sends its operations, affected evidence, and required scope context to OpenAI.
Only a strict `allow` permits SQLite to open the write transaction. A `block`,
refusal, timeout, malformed response, or provider failure returns structured
feedback and leaves the database unchanged. Retrying an unchanged proposal
reuses a current fingerprint-bound `allow` or `block` decision without another
paid call; changing the proposal invalidates it. Provider trouble produces no
decision, so a deliberate retry can try the provider again. The response runs
in background/store mode so the application can poll that same response, with
no automatic paid retry or fallback model.

One write may explicitly set `bypassAiReview=true`. That bypass permits the
manual-only path for that command even when review is configured; it does not
bypass structural validation, affected-node dispositions, context coverage,
fingerprint checks, or atomic-write safeguards. The result records that the
bypass was used.

In the shell-based interface the equivalent one-write flag is simply:

```text
commit --bypass-ai-review
```

To keep a configured key while using manual-only writes, set the kill switch
once:

```powershell
dotnet user-secrets set "AiReview:Enabled" "false" --project src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj
```
