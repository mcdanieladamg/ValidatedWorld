# ValidatedWorld Technical Design

**Status:** Technical requirements subordinate to `README.md`

The README is the authority for product meaning and terminology. This document
turns that vision into implementation constraints. If the two disagree, stop,
follow the README, and reconcile this document before writing dependent code.
The ordered work and actual project status live in
[development_plan.md](development_plan.md).

## 1. Product boundary

ValidatedWorld is a headless .NET 10 application for reviewing changes to one
human-readable dependency graph stored in an embedded SQLite `.vw.db` file.
The application deterministically checks graph structure, selects the complete
affected set described by explicit relationships, presents the relevant scope
context, and writes an approved change atomically. A human performs the manual
review obligations. When the optional semantic reviewer is configured and
enabled, its decision additionally gates the exact write attempt unless that
single command explicitly bypasses AI review.

The initial MVP is local and text-oriented. Its application language, command
text, diagnostics, and AI prompts are hardcoded in English. Localization
infrastructure is out of scope. A GUI, web service, hosted collaboration,
multi-agent coordination, image/OCR ingestion, finished-document generation,
and domain-specific profiles are also outside the initial roadmap.

The database contains the current project, not a project history. An unfinished
change exists only in the running process and is lost when that process exits.

## 2. Canonical graph

### 2.1 Identity and values

Project, node, and edge IDs are stable, opaque text. They use ordinal,
case-sensitive equality and ordering. Validation must reject empty,
whitespace-only, control-character-containing, or over-limit IDs, but must not
silently trim, case-fold, or rewrite them. The implementation will choose and
test conservative length limits.

Optional attributes support only deterministic scalar values: text, signed
integer, canonical base-10 decimal, Boolean, symbol, and UTC instant. An
ID-looking attribute value has no graph behavior. Every meaningful connection
between graph entities is an edge.

A canonical decimal uses ordinary base-10 text with no exponent, leading plus,
unnecessary leading zero, trailing fractional zero, or negative zero. A UTC
instant has zero offset. Public factories accept caller collections in any order
and materialize tags and attribute keys in ordinal order; callers should not
have to pre-sort data to create a node or edge.

### 2.2 Nodes and edges

A node contains:

- a stable ID;
- non-empty human-readable text; and
- optional kind, tags, and scalar attributes.

Kinds and tags help authors search and organize a graph. The common engine does
not assign them hidden semantics. Tags are canonical, ordinal case-sensitive
labels. The application exposes both broad case-insensitive substring search
across entity text and metadata and exact case-sensitive tag lookup across nodes
and edges. Exact tag lookup is a bounded view operation only: it does not create
review arcs, alter scope, or filter the required affected/context sets. Full
node and edge values, including tags, remain visible in read and affected
results. Replacing an entity to change its tags is an ordinary direct change.

An edge contains:

- a stable ID in the same identity space as node IDs;
- source and target node IDs;
- a non-empty human-readable relationship label;
- an explicit review direction; and
- optional rationale, tags, and scalar attributes.

Review direction is workflow metadata:

```text
none              changing either endpoint does not propagate across this edge
source-to-target  changing the source selects the target
target-to-source  changing the target selects the source
both              changing either endpoint selects the other
```

Traversal is recursive, so a selected node can select further nodes through
their declared relationships. This is how a change may have downstream,
upstream, or lateral consequences. Labels do not imply direction, and database
foreign-key direction does not imply meaning.

### 2.3 Purpose and scope

Each project has one purpose node containing its thesis, purpose, scope, or
governing statement. Every other node has exactly one outgoing `scope-parent`
edge whose target is its parent. Together those edges form an acyclic spanning
tree ending at the purpose.

Every changed or affected node contributes its complete scope-upstream lineage,
including the purpose, to mandatory semantic review context. Cross-links can
select nodes in other branches, so one proposal may include several such
lineages. Context is recomputed recursively as the affected set grows.

Including an ancestor as context does not make it a propagation seed and does
not select the ancestor's other children. Directly changing a scope node does
select its descendants. Directly changing the purpose selects the full project.
If the user edits a context ancestor during review, it becomes a direct change
and the affected set is recalculated normally.

`scope-parent` is reserved. Its edge orientation is child to parent and its
review direction is `none`; the special context and descendant rules above
control its review behavior.

Adding, removing, or redirecting a `scope-parent` edge selects the old and new
child endpoints and their current and proposed descendant subtrees. The old and
new immediate parents become non-direct affected review obligations because
their membership changed. They do not become scope-expansion roots, so unrelated
siblings remain excluded unless an explicit semantic review arc reaches them.
Both old and new ancestry lineages are included as context.

### 2.4 Project graph

The immutable in-memory graph contains project ID, title, purpose-node ID,
creation/update timestamps where relevant, nodes, and edges. Collections and
diagnostics use deterministic ordinal ordering. The graph must be usable with
no enabled profile or domain ontology.

### 2.5 New-world bootstrap and trust boundary

The public new-project operation creates exactly one purpose node and no edges.
Every later node or edge, including the first usable tree structure, enters
through the ordinary change session, affected analysis, manual review, and
configured semantic-review gate. A public caller cannot initialize an already
populated graph and thereby establish unreviewed canonical content.

Opening an existing `.vw.db` is different: after structural validation and
fingerprint verification, the file is treated as previously reviewed canonical
state. It is not resubmitted to an AI provider. Built-in samples and test
fixtures may use an internal trusted loader, but that loader is not a public
authoring or import path.

## 3. Validation results

Deterministic validation returns one of three outcomes:

- `valid`: every applicable deterministic check completed and passed;
- `invalid`: a deterministic check found a violation and reports evidence; or
- `inconclusive`: a configured bound, cancellation, unavailable optional
  capability, or internal failure prevented a complete result.

The common validator checks at least:

1. IDs and local value encodings.
2. Global node/edge ID uniqueness.
3. Non-empty project title, node text, and relationship labels.
4. Existing node endpoints for every edge.
5. One existing purpose node.
6. No scope parent for the purpose.
7. Exactly one scope parent for every other node.
8. Acyclic scope lineages that all reach the purpose.
9. `ReviewDirection.None` on every `scope-parent` edge.

Unknown kinds, non-reserved relationship labels, tags, and attributes are valid.
Missing facts and links are unknown, not false.

## 4. In-memory changes and review

### 4.1 Session and operations

The MVP supports one active change session per opened project/process. A session
contains its ID, author/intent, database identity, base-state fingerprint, final
operation per entity ID, projected graph, affected analysis, review state, and
status. It is never stored in the project database.

Operations add, replace, or remove a complete node or edge. A replacement keeps
the same entity ID. One final operation per entity ID makes the proposal
unambiguous. Removing a node requires explicit removal or redirection of its
incident edges; no graph mutation cascades.

A focus/batch helper may add explicit `scope-parent` edge operations for new
nodes when the parent is unambiguous. It returns the expanded operations for
preview and never invents another semantic relationship.

Projection applies operations to isolated ID-keyed builders, produces a new
immutable graph, and runs complete validation without changing SQLite.

### 4.2 Affected analysis

Affected analysis uses the union of review arcs from both the current and
proposed graph. Removed or redirected relationships therefore cannot hide their
former consequences, and new relationships include their new consequences.

Node operation targets are direct seeds. An edge operation is always displayed
as a direct change; the endpoints selected by its old and new review directions
seed propagation. Directly changed scope-node seeds additionally select their
descendant subtrees from the current and proposed scope trees.

Changed `scope-parent` edges use the special topology rule from section 2.3 even
though their ordinary review direction is `none`: old and new children seed
their current/proposed subtrees, and old and new immediate parents seed semantic
propagation without expanding their other scope children.

Traversal is breadth-first with deterministic ID/edge ordering. It retains edge
evidence and shortest explanation paths. Configured depth, node, or output
bounds return `inconclusive`; required items are never silently truncated. The
primary response reports compact omission counts by reason and only a bounded
sample. A cursor or bounded detail query retrieves omitted identities and
evidence without allowing omission metadata itself to grow with the graph.

After propagation, analysis adds every changed/affected node's complete current
and proposed scope-upstream lineage as context. Context-only ancestors are not
enqueued for propagation.

### 4.3 Review obligations

Every affected node has one current session-only disposition:

- `updated` for a directly changed node;
- `reviewed-no-change` after inspection;
- `not-applicable` with a rationale; or
- `pending`, which blocks the database write.

Context-only ancestors require recorded presentation coverage but do not need
an affected-node disposition unless they are independently affected or edited.
Changing the proposal invalidates any disposition or context coverage whose
node, content, or explanation changed.

The final preview presents the exact operation batch, proposed graph identity,
affected nodes and paths, required scope context, validation result, and pending
items. A human can perform this entire workflow without AI.

## 5. State fingerprints

Use SHA-256 over a deterministic, length-delimited UTF-8 encoding. The encoding
includes project identity/title/purpose, then nodes and edges in ordinal ID
order with all persisted fields. It excludes timestamps and the stored
fingerprint itself.

At minimum the application computes:

```text
state fingerprint          current or proposed graph content
operation fingerprint      base fingerprint plus final ordered operations
affected fingerprint       affected nodes, paths, and required context
disposition fingerprint    proposal + node + path evidence being reviewed
```

Fingerprints are opaque integrity and stale-state tokens. Tests must prove that
insertion order and physically different but logically identical SQLite files do
not change the state fingerprint.

## 6. SQLite persistence

Use `Microsoft.Data.Sqlite.Core` directly with an explicitly pinned
`SQLitePCLRaw.bundle_e_sqlite3` package. There is no ORM or external database
service. Every connection enables and verifies foreign keys, uses parameters,
sets conservative time/size limits, refuses extension loading, and verifies the
application ID, migration checksums, schema, row mapping, and integrity before
writes.

SQLite schema v1 has four `STRICT` tables:

```text
schema_migrations  embedded migration ID, checksum, and application time
projects           one current project row and its state fingerprint
nodes              current node rows
edges              current edge rows and restricted endpoint foreign keys
```

Nodes and edges may use canonical JSON columns for their optional tags and
attributes. That JSON is an internal row encoding. The portable project remains
the `.vw.db` application file described by the README.

Required indexes cover node kind and edges by source/target. A partial unique
index permits at most one `scope-parent` edge per child; application validation
proves exact coverage, acyclicity, and root reachability. Stable read views expose
project, node, edge, scope, and expanded review-arc data.

Use rollback journal by default so a closed project is one file. Backup uses
SQLite's online backup API to a new destination. An application-controlled
`export-sql` command emits deterministic, safely quoted schema/data text for
inspection and external tools. Treat supplied databases as untrusted. Project
text is data and is never executed as SQL.

The final write has a provider preflight followed by a short transaction:

1. Rebuild and validate the proposal and review evidence.
2. Unless AI review is disabled, unconfigured, or explicitly bypassed for this
   one command, obtain or reuse an `allow`/`block` decision bound to the exact
   current fingerprints. Return without opening SQLite unless it is `allow`.
3. Recheck that the in-memory proposal still has the reviewed fingerprints.
4. Open a SQLite `BEGIN IMMEDIATE` transaction.
5. Reload current rows and verify the base fingerprint.
6. Apply explicit operations in foreign-key-safe order.
7. Recheck foreign keys, map/validate the complete result, and calculate the new
   fingerprint.
8. Update the project row and commit once.

No human or AI interaction occurs while the write transaction is open. Busy,
stale, mapping, constraint, I/O, or injected failures roll back all writes.

## 7. Solution and public surface

```text
ValidatedWorld.Core                 immutable graph and change/review values
ValidatedWorld.Validation           indexes, validation, affected traversal
ValidatedWorld.Serialization        command/result DTOs and fingerprint encoding
ValidatedWorld.Application          queries, sessions, review, and commit use cases
ValidatedWorld.Persistence.Sqlite   schema, mapping, transactions, views, backup
ValidatedWorld.Cli                  CLI shell, NDJSON host, and composition root
```

Core has no SQLite, JSON, provider, file, or UI dependency. Application defines
persistence ports in domain terms; SQLite implements them.

The public text/structured surface eventually supports:

```text
project: init, open, verify, status, backup, export-sql
read:    node/edge get and list, text search, exact-tag lookup, scope traversal,
         graph navigation/path
change:  begin, show, focus, expand, apply/patch, affected, review, validate,
         write, discard
ai:      status
sample:  list and create
```

Because a session spans multiple commands, the CLI provides two long-lived
local surfaces over the same Application state. The shell interface retains the
active project, selected entities, exact reference, operation batch, and review
state internally. On entry it selects the purpose root. Its filesystem-like
navigation treats the selected node as the working location: `pwd` renders the
stable-ID scope path, `cd` selects a node or its scope parent, and bounded
`dir`/`ls` output distinguishes scope ancestors, scope descendants, incoming
semantic edges, and outgoing semantic edges. Navigation observes the current
proposal, so uncommitted topology changes are immediately visible, but semantic
edges are never presented as scope children. Its flag-based edit commands
incrementally patch one or a few entity values and
`commit [--bypass-ai-review]` needs no JSON. The NDJSON host retains its strict
explicit-reference request/result contract for AIs, scripts, and integrations.
Its incremental `change.patch` command merges one or a few supplied entity
operations into the current normalized batch; `change.apply` explicitly
replaces that batch. Iterative clients can omit the complete operation batch and
proposed graph from session responses while retaining exact references, counts,
affected evidence, review state, and readiness. They can retrieve the normalized
operations without the whole graph for final preview. Structured results go to
stdout and diagnostics to stderr. Search and navigation are deterministic
bounded graph queries rather than provider calls.

Application exposes both complete-batch replacement and incremental patching.
Incremental patches compose against the current proposal, normalize repeated
edits to one final operation per stable ID, and remove an operation that returns
an entity exactly to its base value. Every resulting proposal still recalculates
validation, affected analysis, fingerprints, and review invalidation.

## 8. Optional OpenAI features

OpenAI is the only provider planned for the initial AI features. Provider choice
and all user-facing instructions/prompts are hardcoded in English. Defaults and
local setup are documented in the README; the application uses .NET
configuration, user-secrets, or process environment.

The planned provider path uses OpenAI Responses background mode and polls the
same response within the configured 1,200-second end-to-end deadline. Returning
tool results or polling the same response is continuation, not a new paid retry.

Both features are optional at runtime and both default enabled, but each is
effective only when an API key is configured. Their separate paid live-test
flags default false. If a role is disabled or unconfigured, the application
reports it inactive and keeps the complete manual workflow usable. A
`change.write` request may also explicitly bypass semantic review for that one
write attempt; all deterministic and manual-review gates still apply.

### 8.1 Development gate for any live-AI task

AI integration work is deliberately different from ordinary tasks:

1. The AI task must be current in the development plan.
2. Before changing code, the developer/agent uses the README's public
   `ai.status` request to check effective configuration. This intentionally
   covers .NET User Secrets as well as `OPENAI_API_KEY` without printing,
   reading back, copying, inferring, acquiring, or setting the key.
3. If the key is absent, stop without attempting the task and ask the human to
   configure it locally. Do not ask the human to paste the key into chat or a
   tracked file.
4. The normal full-solution test command discovers every test. Each live test
   calls OpenAI only when its feature's `LiveTests` flag is explicitly true and
   that feature is enabled with an effective key; otherwise it completes
   without a provider call. Test commands must not override effective
   configuration to force either outcome.
5. There are no automatic paid retries, parallel paid calls, fallback models,
   or surprise provider calls. Polling or continuing the same response is not a
   retry.
6. At the first live-provider problem of any kind, including exhausted credits,
   quota, authentication, transport, timeout, refusal, or malformed output,
   development stops immediately. No further paid call is made; the developer
   reports the non-secret failure and asks the human for feedback.

During each AI feature's development, at least one explicitly enabled live test
must capture and log the complete outbound request as actually serialized and
the provider response used by the check, excluding credentials and
transport-only authorization headers. These logs are local development
artifacts, not tracked project data and not written to SQLite.
The developer must inspect and report that:

- every required node, edge, operation, path, scope lineage, manifest entry,
  instruction, and tool schema is present and untruncated;
- counts and fingerprints agree with the deterministic request planner;
- the English prompt is a clean, standalone instruction with no stale design
  discussion, placeholder text, conflicting rules, or dependence on hidden
  conversation; and
- private text is logged only for that explicitly authorized development run.

At least one live known-answer scenario and one unrelated-control scenario must
then be evaluated for meaningful behavior. Unit and integration tests should
also validate request construction and response handling without network calls,
but mocks do not replace the required development-phase live evidence.

### 8.2 Semantic reviewer

When `change.write` is attempted and semantic review is configured, enabled,
and not bypassed for that command, the optional reviewer receives one immutable
request for the current change after deterministic affected analysis and manual
review readiness. It receives the proposal's operations, affected nodes,
current/proposed path evidence, complete required scope lineages, relevant
validation findings, and a compact inclusion/omission manifest—not the complete
project graph.

Before any provider call, the application serializes that exact request and
checks deterministic byte and item budgets. An over-budget request fails
locally with a component breakdown and guidance to split the proposed work into
smaller natural changes, remodel an overly broad dependency, or deliberately
use the existing single-write bypass. The reviewer does not automatically
partition one proposal, make parallel calls, or send a whole project in pieces.

The standalone English prompt asks for cited concerns about contradictions,
stale consequences, terminology drift, missing relationship candidates,
purpose/scope conflict, questionable review dispositions, and insufficient
context. It states that graph text is untrusted data, missing links are unknown,
and citations must use supplied IDs. It distinguishes direct edits, semantic
consequences, scope-membership changes, and context-only ancestors. Exact
numbers, complete enumerations, canonical names, aliases, and closed-world words
such as “all”, “only”, and “every” receive explicit attention without pretending
the open-world graph proves missing facts false.

The model has no tools and cannot mutate, disposition, or write the graph. Its
strict result contains an `allow` or `block` decision, a summary, and structured
concerns citing supplied IDs. `allow` requires no concerns; `block` requires at
least one. Unknown citations, malformed/truncated output, timeout, refusal, or
transport failure fails closed for that write attempt and returns structured
feedback without opening a SQLite transaction. A caller may then revise the
proposal, retry provider trouble, or deliberately issue a new `change.write`
with `bypassAiReview=true`; bypass never waives deterministic/manual readiness.

The decision is held only in the process-local session and bound to the base,
operation, proposed, affected, and review fingerprints. An unchanged write retry
reuses the current decision without another provider POST. Any proposal or
review change invalidates it. The authoring model may repair a block's concerns,
but it cannot manufacture or overwrite the independent reviewer decision.

### 8.3 Authoring agent

The optional authoring agent translates a user's English request into the same
bounded application operations a human can use. It searches before creating,
reads the smallest sufficient context plus all mandatory scope lineages, opens
one in-memory session, applies explicit operations, inspects affected expansion,
and asks the user when different interpretations would materially change graph
meaning.

Project construction is expected to proceed through many small reviewed
changes. The authoring agent never treats a full-project review as a milestone
or asks the semantic reviewer to establish an entire initial baseline. If a
legitimate local change reaches a broad affected set, the request-size preflight
applies before the provider boundary.

Its authoring instructions use stable broad scope containers and stable IDs;
place volatile counts, lists, names, and conclusions in focused claim nodes;
direct source-of-truth edges toward consumers; prefer roster/aggregate hubs over
sibling cliques; and reserve `both` for genuine mutual reconsideration. It
searches aliases and closed-world wording before changing those claims. An
unexpectedly tiny or huge affected preview triggers model inspection rather
than immediate approval, and a scope reparent must expose its old/new subtrees,
parents, and lineages.

It has strict application tools for project status, search/navigation, session
operations, affected analysis, validation, review handoff, exact confirmation,
write, and discard. It has no raw SQL, direct canonical write, automatic review
disposition, or unguarded write tool. Its normal write tool does not set the
single-attempt AI bypass; bypass remains an explicit caller choice.

Before writing, the application presents the exact proposal and obtains explicit
user approval. The short-lived authorization is bound to database/project
identity; base, operation, proposed, affected, context, and review fingerprints;
conversation/session identity; and expiry. Any change invalidates approval.

The initial intake is English descriptions and text only. The agent must work on
graphs larger than its context window through repeated bounded search; it must
not assume the whole project fits in one prompt.

The public conversational entry point is
`ai-assistant-shell <database>`. It and the provider-independent authoring tool
host retain one process-local session. The model can request approval but cannot
grant it: the shell renders the complete preview, reads the human response
directly, records affected/context review only for an exact `yes`, and creates a
short-lived approval bound to conversation, database/project, expiry, and every
current fingerprint. The guarded authoring write requires that binding and
always uses `BypassAiReview=false`.

### 8.4 Local plugin evaluation

The current OpenAI plugin format can bundle a local stdio MCP server, but the
MVP does not package one. The portable user-selected `.vw.db` remains the
authoritative workspace rather than moving into plugin-private data, and a
generic model-issued MCP call is not accepted as proof of exact human semantic
approval. Packaging also requires a distributable local executable rather than
a source-checkout-relative command. A later plugin may reuse the bounded
authoring tool host after those constraints are satisfied; it must preserve the
same local database ownership, process-local session, exact approval, and
independent review gate.

## 9. Realistic proof scenario

`samples/TechnicalProject` will store reviewed text source and expected public
results, never a populated project database. The application generates a
disposable database from those assets.

The baseline models an offline privacy-preserving sensor with purpose plus
power, privacy, documentation, and accessibility branches. Ordinary text nodes
represent requirements, definitions, assumptions, evidence, results, decisions,
implementation, verification, and external artifact anchors. Explicit edges
connect consequences.

Required scenario families include:

- a power-assumption change selecting runtime/battery consequences and relevant
  anchors while excluding privacy/accessibility;
- a retention-policy change selecting privacy/architecture/test/documentation
  consequences while excluding power;
- added, removed, and redirected relationships retaining both old and new
  consequences;
- a directly changed scope selecting its subtree;
- a purpose change selecting the whole project;
- incomplete review blocking the write; and
- every injected write failure preserving the exact prior graph/fingerprint.

The common engine is not expected to calculate battery arithmetic or understand
a contradiction. It must select the correct review surface and require a
thinking participant to resolve it.

Offline performance validation may use synthetic graphs up to roughly 100,000
nodes and 1,000,000 review arcs. It must not invoke an AI provider. Report the
hardware and measured operation without treating one measurement as universal.
