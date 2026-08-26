# ValidatedWorld Development Plan

**Current task:** T9 — Complete CLI/NDJSON manual workflow

**Current task estimate:** gigantic

**Last updated:** 2026-08-26

This is the neutral implementation checklist and handoff record for humans and
coding agents. It contains exactly one current task. The README defines the
product, [technical_design.md](technical_design.md) defines technical behavior,
and this file defines the order of work and records what actually happened.

## 1. How to use this plan

When asked to continue implementation, complete only the task named at the top
of this file. Do not begin a later task, even if it appears easy or closely
related. A human reviews and merges one task before invoking the next developer.

Before starting:

1. Read `README.md`, `AGENTS.md`, this file, and the technical-design sections
   named by the current task.
2. Inspect the current source, tests, and working-tree changes.
3. Confirm that the current task is not already complete.
4. If an acceptance criterion requires a material product choice not settled by
   the README or technical design, ask the human and stop instead of guessing.

On success:

1. Implement only the current task and its tests.
2. Perform the task's realistic smoke test when a usable public surface exists.
3. Run the completion checks in section 2.
4. Change the task's status to `complete` in the table below and add a concise
   evidence entry under Completed evidence.
5. Change **Current task** at the top to the next numbered task. The next task is
   already specified here; refine it only when completed evidence requires it.
6. Set **Current task estimate** directly below it to exactly `small`, `medium`,
   `large`, or `gigantic` based on the next task's expected code-change volume
   as one phase. Use `None` when there is no authorized Current task.
7. Report the result, exact checks, smoke-test findings, uncertainty, Current
   task estimate, and next task to the human, then stop.

On failure:

- Leave Current task and its status unchanged.
- Do not weaken or delete an acceptance criterion to make the task pass.
- Report the failing command, useful output, likely cause, and repairs tried.
- Leave a concise note under Attempt evidence only if it will help the next
  developer and does not contain secrets or a noisy transcript.
- Stop and wait for a new human prompt.

## 2. Testing, smoke checks, and bounded repair

Every coding task adds meaningful automated tests for changed behavior. Prefer
small unit tests for pure rules, integration tests at persistence/process
boundaries, and end-to-end tests once the public CLI exists. Tests must use
realistic connected graph data, not only isolated placeholders.

Also perform an informal user-style smoke check:

- Before a CLI exists, exercise the new behavior through its public API or a
  focused sample harness and record what a caller can actually accomplish.
- After a CLI exists, start from public help and use a disposable
  application-created database. Do not rely on private APIs or direct canonical
  SQL writes.
- Keep those entry conditions as a repeatable spine, but do not reduce smoke QA
  to a fixed script or duplicate the automated suite. Add creative one-off probes
  based on the changed behavior and anything confusing observed during the run.
- Approach the workflow like a curious human: vary plausible inputs or order,
  make a natural mistake when useful, inspect diagnostics and recovery, and try
  an alternate path a user might reasonably choose.
- Fix a finding when the repair is clearly in-scope, small, and straightforward,
  and add a regression test. Escalate to the human if it requires a material
  product, schema, dependency, provider, or scope decision, exposes an inherent
  contradiction, or has no clear low-risk repair.
- Record the goal, repeatable commands/public calls, exploratory probes, observed
  outcomes, confusing behavior, unrelated-node exclusions when relevant, and
  confidence. Exploratory probes need not be standardized across tasks.

After implementation, automated checks, and smoke QA are complete, set the
single **Current task estimate** field near the top for the newly selected task.
Do not also copy the estimate into completed evidence or the handoff template.
The estimate is about code-change breadth for that task as one phase rather than
elapsed time and does not authorize starting, splitting, or redesigning it:

- `small`: localized change with a narrow test surface;
- `medium`: several related changes within one primary subsystem;
- `large`: broad changes spanning multiple components or public behaviors;
- `gigantic`: unusually wide change across many contracts, state paths, or
  integration boundaries, with correspondingly extensive tests.

Documentation-only tasks require link/format/consistency checks, not invented
production tests.

During development, run focused tests as needed. When the task appears ready,
run this sequence once:

```powershell
dotnet restore ValidatedWorld.slnx
dotnet build ValidatedWorld.slnx --no-restore
dotnet test ValidatedWorld.slnx --no-build --no-restore
```

If restore reports unauthorized access to the user-level NuGet configuration,
follow the `AGENTS.md` NuGet restore permission workaround: rerun only the
same restore command with elevated, outside-sandbox permission, without
inspecting or copying that configuration. Keep build and test sandboxed after
restore succeeds.

If a changed-behavior test or full check fails, make at most two materially
different repair attempts for the same failure. Never repeat an unchanged
command merely hoping for a different result. An infrastructure or dependency
failure gets one diagnostic retry after a concrete repair. If the same blocker
remains, record it and stop. Do not enter an open-ended edit/test loop.

Ordinary tests are offline and deterministic. Live OpenAI tests follow the
additional key, opt-in, logging, cost, and no-retry rules in technical design
section 8.

## 3. Repository and scope guardrails

- Preserve unrelated human changes and never overwrite work just to simplify a
  task.
- Do not create or switch branches; stage, commit, merge, rebase, cherry-pick,
  revert, reset, clean, or stash; alter Git configuration; contact remotes;
  push; or open a pull request. Read-only status/diff/log commands are allowed.
- Do not delegate or launch another agent. Development is sequential and
  human-invoked.
- Do not add a new project, dependency, persistence mechanism, provider, UI, or
  domain feature unless the current task explicitly requires it.
- Do not modify the README's product language as a routine implementation
  cleanup. Report a conflict to the human.
- Never search for, expose, copy, or configure credentials.

In product prose, “write” or “apply” means the ValidatedWorld SQLite transaction.
It does not authorize a Git commit.

## 4. Progress

| Task | Status | Purpose |
|---|---|---|
| T0 | complete | Repository scaffold and consolidated documentation |
| T1 | complete | Common immutable graph domain |
| T2 | complete | Graph index and structural validation |
| T3 | complete | Change operations and projection |
| T4 | complete | Affected-set analysis and manual review |
| T5 | complete | Structured protocol and deterministic fingerprints |
| T6 | complete | SQLite current-state persistence and first public read slice |
| T7 | complete | Application queries and in-memory session lifecycle |
| T8 | complete | Atomic write and rollback behavior |
| T9 | pending | Complete CLI/NDJSON manual workflow |
| T10 | pending | Realistic MVP scenarios and usability hardening |
| T11 | pending | MVP release evidence and stop decision |
| T12 | optional | OpenAI semantic reviewer, only after human authorization |
| T13 | optional | OpenAI authoring agent, only after human authorization |

## 5. Completed evidence

### T0 — repository scaffold and documentation

Completed 2026-08-13.

- Created the .NET 10 solution/project skeleton for Core, Validation,
  Serialization, Application, SQLite persistence, CLI, and initial test projects.
- Pinned the embedded SQLite managed/native packages and added a CLI
  `UserSecretsId`.
- Consolidated the product's technical requirements into one technical design
  subordinate to the human-edited README.
- Established this one-task neutral plan, bounded retry/testing rules, no-Git and
  no-delegation boundaries, human handoff, optional-AI key gate, English-only
  scope, and required live-request inspection.
- Verified on 2026-08-13 that all Markdown links resolve, code fences balance,
  instruction/document lines stay within 120 characters, obsolete document
  references are absent, and the tracked diff has no whitespace errors.
- `dotnet restore ValidatedWorld.slnx`, `dotnet build --no-restore`, and
  `dotnet test --no-build --no-restore`
  succeeded on 2026-08-13. The scaffold build had 0 warnings and 0 errors;
  all 5 existing scaffold tests passed.
- No production feature is implemented. `Class1` files, `Hello, World!`, and
  placeholder tests are scaffold only.

### T1 — common graph domain

Completed 2026-08-14.

- Replaced the Core placeholder with immutable public `ProjectId`, `EntityId`,
  `GraphValue`, `GraphAttribute`, `GraphNode`, `GraphEdge`, and `ProjectGraph`
  types plus the four-value `ReviewDirection` enum.
- IDs use ordinal equality and ordering, reject empty/whitespace/control text,
  and use a 256-character bound. Conservative model bounds are 16,384
  characters for text, 1,024 for relationship labels, 256 for metadata names,
  and 256 for canonical decimals.
- Scalar values cover text, signed integer, canonical decimal, Boolean, symbol,
  and zero-offset UTC instant. Decimal construction rejects exponent, plus sign,
  unnecessary leading zero, trailing fractional zero, and negative zero forms.
- Nodes and edges defensively copy and ordinal-sort tags and attributes, reject
  duplicate metadata keys/tags, preserve unknown kinds/labels, and keep node
  and edge IDs in the same `EntityId` identity space. Graph collections are
  copied and deterministically sorted by ID.
- Kept graph-wide identity, endpoint, purpose, and scope-tree checks for T2.
  In particular, Core can construct a `scope-parent` edge with any explicit
  review direction so the T2 validator can report malformed reserved edges.
- Added a public-API-only `TechnicalProjectGraphBuilder` test fixture with a
  purpose, sibling power/privacy scopes, ordinary concepts, scope edges,
  directed semantic cross-links, and an external anchor.
- Focused check passed with 5 tests using
  `dotnet test tests/ValidatedWorld.Core.Tests/ValidatedWorld.Core.Tests.csproj
  --no-restore`.
- Public API smoke: the filtered `Technical_project_builder` test constructed
  the baseline graph and observed its purpose, scope-parent, semantic direction,
  and external-anchor data successfully.
- Full checks on 2026-08-14: `dotnet restore ValidatedWorld.slnx`,
  `dotnet build ValidatedWorld.slnx --no-restore` (0 warnings, 0 errors), and
  `dotnet test ValidatedWorld.slnx --no-build --no-restore` (9 passed).
- Modeling friction was low: explicit review direction is clear at edge
  construction, while optional metadata is slightly verbose because duplicate
  keys are rejected from caller sequences before ordinal canonicalization.
  The chosen bounds are conservative implementation choices and should remain
  visible when T2/T5 add diagnostics and protocol limits.

### T2 — graph index and structural validation

Completed 2026-08-18.

- Added the public `GraphIndex` with duplicate-preserving node/edge groups,
  unique ID maps, source/target edge indexes, scope-parent and scope-child
  indexes, breadth-first scope descendants, scope-upstream paths, and expanded
  non-scope review arcs for all four review directions.
- Added `GraphValidator`, `GraphValidationResult`, deterministic diagnostic
  records, and valid/invalid/inconclusive outcomes. Validation covers global
  node/edge identity collisions, endpoints, purpose, exact scope-parent
  coverage, reserved edge direction, cycles, and purpose reachability.
- Added configurable traversal-depth/node/diagnostic bounds and cancellation;
  bounded or cancelled validation returns inconclusive with omission evidence.
- Added four focused tests covering the TechnicalProject graph, index
  navigation without sibling fan-out, review direction expansion, structural
  violations, deterministic diagnostics, cancellation, and bounds.
- Public API smoke: validating the TechnicalProject graph returned `Valid`,
  exposed two purpose-level sibling scopes, returned the expected six
  descendants and retention-to-purpose path, and expanded only the two
  non-scope review arcs.
- Full checks on 2026-08-18: `dotnet restore ValidatedWorld.slnx`,
  `dotnet build ValidatedWorld.slnx --no-restore` (0 warnings, 0 errors), and
  `dotnet test ValidatedWorld.slnx --no-build --no-restore` (13 passed).
- Modeling friction: malformed graphs remain indexable so diagnostics can be
  reported; scope-parent navigation stops deterministically at ambiguity,
  missing nodes, or cycles. No product or persistence dependencies were added.

### T3 — change operations and projection

Completed 2026-08-18.

- Added immutable `GraphOperation`, `GraphOperationBatch`, and operation/entity
  kind values for add, replace, and remove node/edge changes. Batches reject
  duplicate entity IDs and sort final operations deterministically.
- Added `GraphProjector` and structured operation exceptions for wrong entity
  kinds and add/replace/remove preconditions. Projection uses isolated maps,
  preserves stable replacement IDs, does not cascade node removal, and returns
  complete structural validation for the proposed graph.
- Added `GraphOperationFocus` with explicit `ScopeParentSelection` expansion
  for newly added nodes only. It rejects ambiguous parents and never invents
  semantic cross-links or edge IDs.
- Added six focused tests covering all operation kinds, deterministic batches,
  wrong kinds, preconditions, incident-edge handling, invalid proposals, valid
  one-batch repairs, focus ambiguity, supplied scope parents, and base-graph
  immutability. Focused Validation tests passed: 10.
- Public API smoke: a TechnicalProject proposal replaced the battery
  assumption, added a scoped requirement, redirected a dependency, removed a
  relationship, projected as valid, and left the base graph unchanged.
- Full checks on 2026-08-18: `dotnet restore ValidatedWorld.slnx`,
  `dotnet build ValidatedWorld.slnx --no-restore` (0 warnings, 0 errors), and
  `dotnet test ValidatedWorld.slnx --no-build --no-restore` (18 passed).
- Modeling friction: explicit operation payloads make stable replacement and
  no-cascade removal clear; new nodes still need an explicit scope-parent
  operation, with the focus helper providing a bounded convenience for an
  unambiguous parent.

### T4 — affected-set analysis and manual review

Completed 2026-08-19.

- Added deterministic affected analysis over the union of current and proposed
  review arcs, including edge-operation changes, direct node seeds, scope-node
  descendant expansion, breadth-first shortest explanations, and current/
  proposed scope-upstream context.
- Added explicit depth, affected-node, output, and cancellation omissions that
  make bounded analysis inconclusive without silently truncating required work.
- Added process-local review sessions with updated, reviewed-no-change,
  not-applicable, and pending dispositions; required context presentation;
  readiness blockers; and evidence-based staleness invalidation on refresh.
- Added 9 focused T4 tests covering review directions, edge changes, scope/root
  expansion, multiple chains, cycles, unrelated-branch exclusion, bounds,
  manual review readiness, staleness, and a public-API smoke walkthrough.
- Public API smoke completed a revised battery proposal, reviewed its runtime
  consequence, presented purpose/power context, and reached write readiness.
- Full checks on 2026-08-19: `dotnet restore ValidatedWorld.slnx`,
  `dotnet build ValidatedWorld.slnx --no-restore` (0 warnings, 0 errors), and
  `dotnet test ValidatedWorld.slnx --no-build --no-restore` (27 passed).
- Modeling friction: changing a scope node intentionally selects its complete
  descendant branch and then follows modeled semantic consequences; unrelated
  sibling scopes remain excluded unless a relationship or direct change reaches
  them.

### T5 — structured protocol and deterministic fingerprints

Completed 2026-08-26.

- Replaced the Serialization placeholder with strict System.Text.Json options,
  versioned request/result envelopes, and explicit DTOs for graphs, nodes,
  edges, scalar values, operations, batches, and bounded validation diagnostics.
- Added public graph and operation DTO conversion with Core construction on
  decode, preserving canonical collection ordering and rejecting malformed
  values through the existing domain rules.
- Added deterministic length-delimited SHA-256 fingerprints for current/
  proposed graph state, ordered operations, affected analysis, and review
  dispositions. Encoding includes all graph fields and evidence while excluding
  timestamps and stored tokens; integer byte order is explicit.
- Added 4 focused protocol tests covering graph/value round trips, strict
  unknown-member rejection, insertion-order-independent state/operation
  fingerprints, one-field hash changes, and affected/disposition separation.
- Public API smoke: serialized and restored a graph containing tags, attributes,
  scope-parent data, and operations; analyzed a replacement and confirmed the
  affected and disposition tokens changed at the expected boundary.
- Full checks on 2026-08-26: `dotnet restore ValidatedWorld.slnx` (required the
  documented elevated NuGet.Config workaround), `dotnet build
  ValidatedWorld.slnx --no-restore` (0 warnings, 0 errors), and `dotnet test
  ValidatedWorld.slnx --no-build --no-restore` (29 passed).
- Modeling friction: the protocol keeps graph IDs and scalar values as explicit
  typed fields rather than relying on JSON representations of Core structs;
  this makes malformed input fail at the domain boundary and keeps fingerprints
  independent of JSON property or collection ordering.

### T6 — SQLite current-state persistence and first public read slice

Completed 2026-08-26.

- Added Application-owned persistence ports and public initialize, load, status,
  verify, backup, and sample-creation use cases. Added the built-in
  `technical-project` sample through public graph APIs rather than a populated
  database fixture.
- Added the fixed four-table SQLite v1 migration with checked application ID,
  user version, migration checksum, exact schema objects, `STRICT` tables,
  endpoint foreign keys, required indexes, the partial scope-parent index, and
  project/node/edge/scope/review-arc read views.
- Added bounded canonical row mapping, structural validation, logical state-
  fingerprint verification, integrity and foreign-key checks, rollback-journal
  and read-only connection policies, parameterized initialization, and verified
  online backup to a new destination. Initialization and backup fully verify a
  unique temporary database before exposing its final filename.
- Replaced the CLI placeholder with help plus minimal `project init`, `status`,
  `verify`, and `backup` and `sample list`/`create` commands. Existing
  destinations are not overwritten, and paths containing spaces work.
- Added 7 persistence behavior tests; the focused persistence project passed 8
  tests including its existing assembly test. Coverage includes fresh/reopen
  and byte-preserving reads, checked header/version/migration/schema rejection,
  corrupt/malformed/oversized rows, strict tables, foreign keys, views/indexes,
  physical row reinsertion order, backup equivalence, and safe rejection of
  invalid graphs and existing destinations.
- Public CLI smoke: followed help to list/create `technical-project` in a
  disposable path containing spaces, read status, verified all nine checks,
  made an online backup, and read the backup. Both files reported 7 nodes, 8
  edges, schema v1, SQLite 3.53.3 from the bundled runtime, and state fingerprint
  `c407e3498283ced8234835dc5dcce93d468e5b59b691d8ee235dcfbd96a7dba9`.
- Full checks on 2026-08-26: `dotnet restore ValidatedWorld.slnx` succeeded with
  the documented elevated NuGet.Config workaround; `dotnet build
  ValidatedWorld.slnx --no-restore` succeeded with 0 warnings and 0 errors; and
  `dotnet test ValidatedWorld.slnx --no-build --no-restore` passed all 37 tests.
- Usability friction was low for the first slice: commands expose concise
  key/value results and storage failures have stable codes. Status is
  intentionally only the first read summary; bounded node, edge, search, scope,
  and navigation queries remain T7.

### T7 — application queries and in-memory session lifecycle

Completed 2026-08-26.

- Added verified immutable project-query snapshots with node/edge get and list,
  combined text search, scope lineage and descendants, graph neighbors,
  directional review dependencies, shortest dependency paths, and multi-node
  scope context without sibling fan-out.
- Added deterministic state-bound cursors, page limits, traversal depth/node/
  cancellation limits, and explicit output/traversal omissions. Wrong project,
  entity, and cursor inputs report stable application query errors.
- Added the process-local begin/show/focus/expand/apply/affected/review/validate/
  discard lifecycle with one active session per project in the application
  coordinator, controlled clocks and IDs, exact base/operation/proposal/
  affected/review fingerprints, external base-state rechecks, review evidence
  refresh, and explicit unresolved-session exit warnings.
- Proposal and review actions use only verified loads and immutable projection;
  no session data is persisted. Tests compare the complete SQLite file bytes
  before and after proposal, replacement, review, validation, and discard.
- Added 5 focused T7 behavior tests plus the existing Application assembly test;
  the focused Application suite passed all 6 tests. Coverage includes bounded
  cursors and omissions, unrelated-sibling exclusion, wrong project/session
  IDs, stale references and canonical state, replacement invalidation, focus,
  discard, process-exit loss, and unchanged SQLite.
- Public Application API smoke: created `technical-project` in a disposable path
  containing spaces; queried scope/dependencies; revised and reviewed the
  battery assumption; deliberately reused a stale reference and a wrong
  project/session; exercised add-with-scope focus and an intentionally bounded
  analysis; recovered, discarded, and confirmed byte-identical SQLite state.
  Diagnostics were clear, unrelated privacy nodes stayed excluded, and no
  confusing or material modeling issue was found.
- Full checks on 2026-08-26: `dotnet restore ValidatedWorld.slnx` succeeded with
  the documented elevated NuGet.Config workaround; `dotnet build
  ValidatedWorld.slnx --no-restore` succeeded with 0 warnings and 0 errors; and
  `dotnet test ValidatedWorld.slnx --no-build --no-restore` passed all 42 tests.

### T8 — atomic write and rollback behavior

Completed 2026-08-26.

- Added a reviewed write request/result contract and `WriteChange` use case.
  It blocks non-ready review states, returns structured written/stale/busy/
  failure outcomes, removes the in-memory session only after success, and keeps
  it available after every non-success result.
- Added the SQLite `BEGIN IMMEDIATE` write path. It verifies the current state
  inside the transaction, projects and validates the final batch again, removes
  edges before nodes, writes nodes before edges, checks foreign keys, reloads
  and validates the full graph, updates the state fingerprint once, then commits.
- Added deterministic fault-injection points around every transaction/write
  boundary. A failed SQLite setup now also disposes its connection, including
  contention during connection policy setup.
- Added 6 focused Application behavior tests; the focused Application suite
  passed all 12 tests. Coverage includes successful replacement and explicit
  edge/node removal, pending/invalid/inconclusive proposals, stale state,
  bounded concurrent-writer busy behavior, rollback at every injected boundary,
  and a validated retry after a one-time failure.
- Public Application API smoke: created disposable `technical-project` databases
  through the normal sample path, reviewed and wrote a battery-assumption
  revision, then separately removed its incident edges and node. Deliberate
  unreviewed, structurally invalid, bounded, stale, busy, and injected-fault
  attempts left the canonical database unchanged and retained the session;
  retrying a recovered session committed successfully. Diagnostics were clear,
  and no material usability or modeling issue was found.
- Full checks on 2026-08-26: `dotnet restore ValidatedWorld.slnx` succeeded with
  the documented elevated NuGet.Config workaround; `dotnet build
  ValidatedWorld.slnx --no-restore` succeeded with 0 warnings and 0 errors; and
  `dotnet test ValidatedWorld.slnx --no-build --no-restore` passed all 48 tests.

## 6. Current and remaining tasks

### T1 — common graph domain

**Read:** Technical design sections 1, 2, 3, and 9.

**Goal:** Replace the Core placeholders with the small immutable model needed to
represent a profile-free human-readable graph. Keep this task independent of
SQLite, JSON, traversal, application sessions, CLI, and AI.

**Implement:**

- Stable `ProjectId` and `EntityId` value types with ordinal equality/ordering,
  explicit construction validation, and conservative length/control rules.
- Deterministic scalar graph values for text, integer, canonical decimal,
  Boolean, symbol, and UTC instant.
- Immutable `GraphNode`, `GraphEdge`, and `ReviewDirection`.
- Immutable `ProjectGraph` with project ID, title, purpose-node ID, nodes, and
  edges in deterministic order.
- Local construction checks: valid IDs, non-empty text/title/relationship,
  canonical values, and canonical ordinal tag/attribute collections regardless
  of caller insertion order.
- A realistic Core test builder for the TechnicalProject baseline: one purpose,
  at least two sibling scopes, ordinary text concepts, scope-parent edges,
  directed semantic cross-links, and an external anchor. It must use only public
  Core APIs and no profile/test escape hatch.

**Do not implement:**

- global endpoint, uniqueness, purpose, or scope-tree validation (T2);
- graph indexes or affected traversal;
- operations, projection, sessions, or review dispositions;
- fingerprints or JSON DTOs;
- SQLite, file I/O, CLI behavior, AI, or profiles.

**Tests and acceptance:**

1. Every public ID/value/entity constructor has representative valid cases and
   rejected empty, malformed, noncanonical, duplicate-key, or over-limit
   boundaries; equivalent collection insertion orders produce the same model.
2. Node and edge IDs share one conceptual entity-ID type.
3. Unknown node kinds and relationship labels remain representable.
4. A graph with no optional attributes is easy to construct.
5. The TechnicalProject test graph is constructed through public Core APIs and
   asserts the intended source/target/direction data without asking Core to run
   graph-wide validation.
6. Core has no persistence, serialization, network, provider, or UI dependency.
7. Focused tests and the full restore/build/test sequence pass with zero build
   warnings.
8. The report records any friction in IDs, edge direction, or optional metadata.
9. Mark T1 complete, set Current task to T2, report, and stop.

### T2 — graph index and structural validation

**Read:** Technical design sections 2 and 3.

**Goal:** Build deterministic indexes and prove whether a complete graph has the
required endpoints, purpose, and scope tree.

**Implement:** ID maps; edges by source/target; scope parent/children; expanded
non-scope review arcs; valid/invalid/inconclusive result and precise diagnostic
records; global identity/endpoint checks; purpose and scope-tree validation; and
configured traversal limits/cancellation.

**Tests and acceptance:** Cover duplicate node/edge identity, missing endpoints,
missing/multiple parents, parented purpose, self/cyclic/disconnected scope,
malformed scope direction, deep valid trees, unknown kinds/labels, deterministic
diagnostic ordering, and TechnicalProject validation. Validate that scope
ancestors and descendants can be queried without sibling fan-out. Run the public
API smoke check and full checks. Mark complete, select T3, report, and stop.

### T3 — change operations and projection

**Read:** Technical design sections 2, 3, and 4.1.

**Goal:** Express and validate a proposed batch entirely in memory.

**Implement:** Add/replace/remove node and edge operations; one final operation
per entity ID; deterministic operation ordering; projection over immutable base
graphs; explicit incident-edge handling for node removal; validation of the
projected graph; and focus/batch expansion that adds only unambiguous explicit
scope-parent operations.

**Tests and acceptance:** Cover every operation kind, wrong entity kind,
add/replace/remove preconditions, conflicting operations, stable replacement
IDs, no cascading delete, valid repair in one batch, invalid projected graph,
focus ambiguity, supplied scope parents, and proof that no semantic cross-link is
invented. Smoke a realistic proposal and verify the base graph remains unchanged.
Run full checks, mark complete, select T4, report, and stop.

### T4 — affected-set analysis and manual review

**Read:** Technical design sections 2.3 and 4.

**Goal:** Select and explain the complete modeled review surface for a proposal.

**Implement:** Current/proposed review-arc union; node and edge-operation seeds;
directly changed scope descendant selection; deterministic breadth-first paths;
current/proposed scope-upstream context through purpose; explicit bound/omission
results; affected-node dispositions; context-presentation coverage; disposition
staleness; and review-ready validation.

**Tests and acceptance:** Cover all four directions, cycles/multiple paths,
deterministic shortest explanations, added/removed/redirected edges, upward and
lateral propagation, multiple scope lineages, leaf context without siblings,
direct scope/root changes, operation changes invalidating only stale evidence,
pending review blocking readiness, unrelated TechnicalProject exclusions, and
bounds returning inconclusive. Complete a user-style manual review through
public APIs, run full checks, mark complete, select T5, report, and stop.

### T5 — structured protocol and deterministic fingerprints

**Read:** Technical design sections 5 and 7.

**Goal:** Add strict machine-facing command/result contracts and integrity
tokens without changing persistence.

**Implement:** Versioned request/result DTOs for behavior implemented so far;
strict System.Text.Json handling; deterministic length-delimited encoding;
state, operation, proposed, affected, and disposition SHA-256 fingerprints; and
bounded structured diagnostics.

**Tests and acceptance:** Cover round trips, unknown/duplicate/missing fields,
enum/value encoding, malformed input, canonical ordering, Unicode and delimiter
ambiguity, insertion-order independence, one-field hash changes, stable goldens,
and no secret/private diagnostic leakage. Use TechnicalProject request/result
goldens, run a serialization smoke check and full checks, mark complete, select
T6, report, and stop.

### T6 — SQLite current-state persistence and first public read slice

**Read:** Technical design sections 5, 6, 7, and 9.

**Goal:** Create, load, verify, query, and back up a real `.vw.db` with no
external SQLite installation.

**Implement:** Four-table checked migration; application ID/user version;
foreign-key and connection policy; strict mapping/limits; state-fingerprint
verification; initialization; load/status/verify/backup; required indexes/read
views; persistence ports; disposable database helpers; and minimal CLI commands
for init/status/verify/backup/sample creation.

**Tests and acceptance:** Cover fresh/reopen database, migration checksum and
unknown-version rejection, corrupt/malformed/oversized rows, foreign keys,
read-only behavior, logical fingerprint vs physical insertion order, views,
backup equivalence, paths with spaces, bundled native runtime, and no server,
`sqlite3`, ORM, or Docker. Create the TechnicalProject database through public
application/CLI paths and perform the first help-to-read smoke walkthrough. Run
full checks, mark complete, select T7, report, and stop.

### T7 — application queries and in-memory session lifecycle

**Read:** Technical design sections 4 and 7.

**Goal:** Orchestrate bounded graph reads and one process-local proposal without
writing canonical rows.

**Implement:** Node/edge get/list/search; scope/neighbor/dependency/path/context
queries; begin/show/focus/expand/apply/affected/review/validate/discard use cases;
one active session per project/process; base and proposal fingerprint checks;
controlled clocks/IDs; and explicit unresolved-session exit warnings.

**Tests and acceptance:** Cover deterministic bounds/cursors, query omissions,
wrong-project/session IDs, state transitions, replace operations, discard,
process-exit loss, and unchanged SQLite across all proposal/review actions.
Perform a realistic proposal and review from public Application APIs. Run full
checks, mark complete, select T8, report, and stop.

### T8 — atomic write and rollback behavior

**Read:** Technical design sections 4, 5, and 6.

**Goal:** Apply a completely reviewed proposal in one short SQLite transaction
or leave the exact previous graph untouched.

**Implement:** Review-ready preconditions; current base recheck; `BEGIN
IMMEDIATE`; foreign-key-safe explicit writes; final mapping/validation and
fingerprint update; structured busy/stale/failure results; fault injection at
each write boundary; and session state after success/failure.

**Tests and acceptance:** Prove pending/invalid/inconclusive/stale proposals do
not write; every injected fault preserves all prior rows/fingerprint; concurrent
writer behavior is bounded; retry never skips validation; success yields exactly
the expected graph; and only current state remains. Smoke a successful and
faulted TechnicalProject change, run full checks, mark complete, select T9,
report, and stop.

### T9 — complete CLI/NDJSON manual workflow

**Read:** Technical design section 7.

**Goal:** Make the full MVP usable by a human or script without SQL, AI, or a
graphical interface.

**Implement:** Stable English help and commands for all implemented project,
read, change, and sample use cases; a long-lived NDJSON stdin/stdout host for
in-memory sessions; strict exit/status codes; cancellation; stdout result vs
stderr log separation; deterministic safely quoted SQL export; and clear
warnings about session loss.

**Tests and acceptance:** Cover help-driven init-to-write-to-backup workflow,
malformed commands, process lifetime, broken pipe/cancellation, exit codes,
structured output stability, paths/quoting, no session persistence, and no
provider contact. Verify exported SQL contains the complete current graph and no
secret/session data. Perform and record a fresh black-box smoke walkthrough
using only public help and a disposable generated database. Run full checks,
mark complete, select T10, report, and stop.

### T10 — realistic MVP scenarios and usability hardening

**Read:** Technical design section 9 and all public workflow sections.

**Goal:** Turn TechnicalProject into a reusable proof of affected precision,
review burden, rollback safety, and ordinary usability.

**Implement:** Reviewed source assets, operation batches, public-result goldens,
and goals for every scenario family in technical design section 9; reusable test
helpers; end-to-end tests; bounded diagnostics; and fixes for deterministic
defects found during user-style walkthroughs.

**Tests and acceptance:** Execute power, privacy, edge-change, scope/root,
incomplete-review, stale, rollback, backup, and unrelated-control scenarios.
Record modeling effort, number of edges/review items, confusing terminology,
false-positive/omitted affected nodes, and confidence. No populated sample
database is tracked. Run full checks, mark complete, select T11, report, and
stop.

### T11 — MVP release evidence and stop decision

**Read:** Entire README, technical design, and accumulated evidence here.

**Goal:** Determine honestly whether the manual MVP supports the README's central
promise before adding optional AI.

**Implement/evaluate:** Clean publish/run on supported platforms; package/native
SQLite behavior; representative/expected/stress measurements; configured limits;
full regression and smoke suite; public documentation accuracy; affected-set
precision and review/modeling burden; and comparison with ordinary explicit-link
inspection.

**Acceptance:** Record hardware and results, unresolved defects, usability
concerns, and an explicit recommendation to continue, narrow, pivot, or stop.
Set Current task to `None — MVP evidence complete; human direction required`,
report, and stop. Do not automatically select T12 or T13.

### T12 — optional OpenAI semantic reviewer

**Prerequisites:** T11 complete; explicit human decision to make T12 current;
human-configured local `OPENAI_API_KEY`; and an explicitly enabled live-test flag.
If the key is not configured, make no code changes and ask the human to set it
locally. Never set or display it.

**Read:** Technical design section 8, especially the live-request gate.

**Goal:** Add one optional independent review of a complete proposal while
preserving the full manual workflow.

**Implement:** Deterministic immutable request planner and coverage manifest;
standalone English prompt; strict cited response; in-memory concern handling and
staleness; disabled/unconfigured/inconclusive behavior; one OpenAI Responses
client; explicit per-call authorization; time/usage metadata; and zero automatic
paid retries.

**Tests and acceptance:** Offline unit/integration tests cover exact request
content, disjoint chains, required scope lineages, citations, malformed/refused/
timeout responses, no mutation/write, and manual fallback. An explicitly enabled
live run must log and inspect the exact serialized request without credentials,
then evaluate known-contradiction and unrelated-control cases for prompt quality,
scope correctness, useful concerns, false positives/omissions, tokens, cost, and
latency. Decide with the human whether to retain, revise, or omit the feature;
do not automatically begin T13.

### T13 — optional OpenAI authoring agent

**Prerequisites:** T12 retain/omit decision; explicit human decision to make T13
current; human-configured local `OPENAI_API_KEY`; and an explicitly enabled
live-test flag. If the key is not configured, make no code changes and ask the
human to set it locally. Never set or display it.

**Read:** Technical design section 8, especially the live-request gate.

**Goal:** Add optional conversational English authoring through the same bounded
Application use cases while preserving exact human approval and manual use.

**Implement:** Strict read/change tools; search-before-create loop; bounded
context/tool/operation limits; material questions; affected/review iteration;
new-project and change previews; exact short-lived approval binding; guarded
normal write tool; disabled/unconfigured behavior; one OpenAI Responses client;
and zero automatic paid retries.

**Tests and acceptance:** Scripted tests cover large-graph bounded search,
duplicate/unrelated avoidance, material questions, session loss, review
iteration, stale approval, no SQL/direct write/automatic disposition, and manual
fallback. An explicitly enabled live run must log and inspect the exact prompt,
context, and tool schemas without credentials, then evaluate a known new-project
and existing-project change for correctness, user burden, unrelated mutations,
questions, affected coverage, approval binding, tokens, cost, and latency.
Retain the feature only if it meaningfully reduces burden without bypassing the
README's review/write guarantees.

## 7. Attempt evidence

No unresolved failed current-task attempt is recorded.

## 8. Handoff report template

```text
Task:
Outcome:
Implemented:
Automated tests:
Smoke test:
Usability/modeling findings:
Remaining uncertainty:
Plan now points to:
```
