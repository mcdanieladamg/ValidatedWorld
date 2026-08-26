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
every change made beneath it. However, as an addendum to that, nodes are allowed
to declare other nodes as downstream from them, arbitrarily, so the unique
upstream path of those other notes will also be part of the relevant dependency
graphs for such changes, and that is an iterative process that could produce
several apparent upstream paths for a single node change, alongside the expected
downstream paths which will be pulled for the change as well.

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
structural check passes; SQLite then applies the batch atomically.

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

A `scope-parent` change is itself meaningful topology. Adding, removing, or
redirecting one selects the old and new child subtrees and makes the old and new
immediate parents review obligations; both ancestry lineages are included
without pulling unrelated siblings. The [CLI usage guide](docs/cli_usage.md) and
[lore modeling study](docs/lore_modeling_study.md) give concrete patterns and
measured examples for humans and authoring agents.

## Product boundary

ValidatedWorld owns:

- the authoritative current `project.vw.db` state;
- stable node/edge IDs, text, labels, endpoints, and review direction;
- structural validation and optional profile validation;
- explained affected-subgraph expansion and complete review obligations;
- atomic in-memory-to-SQLite change transactions;
- bounded text-oriented queries and structured command results for humans, AIs,
  and integrations.

ValidatedWorld does **not** own the finished novel, paper, patent application,
manual, source tree, game project, or media. External artifact/anchor nodes may
point to those products, but the engine does not rewrite, render, publish, or
certify them.

The operation batch is the direct description of a pending change. There is no
separate semantic-diff format and no retained commit-history subsystem.

The planned AI support has two separate roles. The optional authoring agent
searches the project and operates the same change tools available to a human.
The optional semantic reviewer independently examines one complete proposed
transaction and its affected context. The reviewer never edits the graph; the
author never approves its own review; and neither turns model judgment into
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
→ independent semantic AI review runs when enabled and available
→ agent repairs the proposal or discusses concerns with the user
→ app shows the exact final operation/affected-set/state-fingerprint preview
→ user approves that exact proposal in the conversation
→ authoring agent calls the guarded commit tool
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

The [CLI usage guide](docs/cli_usage.md) documents the current manual workflow.
Technical requirements and the one-task-at-a-time implementation checklist are
in the [development plan](docs/development_plan.md).
