# ValidatedWorld Development Plan

**Current task:** T15 — bounded semantic database diff

**Current task estimate:** large

This is the single implementation roadmap. `ValidatedWorld.Blueprint.vw.db` is
the canonical detailed product map; this file selects one executable phase and
orders the remaining gaps. The README is the product thesis/bootstrap and
[technical_design.md](technical_design.md) is the precise implementation
contract.

## Development loop

Implement only Current task. Preserve human changes, add focused automated
tests, perform a public-surface smoke check, then run:

```powershell
dotnet restore ValidatedWorld.slnx
dotnet build ValidatedWorld.slnx --no-restore
dotnet test ValidatedWorld.slnx --no-build --no-restore
```

On success, mark the task complete, select the next prewritten task, set its
estimate, update affected canonical database nodes through an ordinary reviewed
change session, and stop for human review. On failure, leave Current task
unchanged and report the command, output, cause, and repairs. Follow all testing,
AI-cost, security, and Git boundaries in `AGENTS.md`.

## Progress

| Task | Status | Purpose |
|---|---|---|
| T0–T14 | complete | MVP, SQLite workflows, review, AI gate, and optional AI authoring |
| T15 | current | Bounded semantic database diff |
| T16 | pending | Reviewed purpose-only project bootstrap |
| T17 | pending | Provider request limits and compact bounded omissions |
| T18 | pending | Graph observability and dependency-quality reports |
| T19 | pending | Ranked lexical retrieval and measured query scaling |
| T20 | pending | Evidence-gated semantic retrieval experiment |
| T21 | pending | Graph-aware three-way merge and optional Git integration |
| T22 | pending | External-artifact drift and renderer adapter evaluation |
| T23 | pending | Large-import and bulk-authoring workflow |
| T24 | pending | Domain profile extension contract |

## T15 — bounded semantic database diff

**Goal:** Make a committed `.vw.db` reviewable without a redundant full JSON or
SQL mirror. Here, “semantic diff” means graph entities and their human-readable
values, not embedding or AI judgment.

**Public contract:**

- Compare two verified `.vw.db` files with the same project ID. Never write
  either database and never call an AI provider.
- Return base/target state fingerprints, project metadata changes, summary
  counts, and stable-ID node/edge additions, replacements, and removals.
- A replacement contains complete old/new values plus deterministic
  `changedFields`; scope-parent endpoint and edge review-direction changes must
  be explicit.
- Order results deterministically by entity category and stable ID. Bound detail
  with `limit` and an opaque cursor bound to both fingerprints and all query
  options. Summary counts remain present on every page.
- Add one-shot CLI
  `project diff <base-db> <target-db> [--limit N] [--cursor TOKEN]` and NDJSON
  `project.diff` with `basePath`, `targetPath`, optional `limit`, and optional
  `cursor`.
- Reject unreadable/invalid databases, a project-ID mismatch, invalid limits,
  and stale or cross-request cursors with actionable errors. Identical databases
  succeed with zero changes; reversing inputs reverses adds/removes and old/new.
- Keep SQL export as an on-demand interchange tool. Do not add a history table,
  committed text mirror, Git filter, custom merge driver, schema migration,
  provider, or new major dependency in this phase.

**Implementation shape:**

- Put deterministic comparison/result values in Core or Application without a
  SQLite dependency. Persistence loads and verifies each immutable snapshot;
  CLI and NDJSON are thin adapters over one Application use case.
- Fingerprint cursors with the established serialization/fingerprint approach.
  Pagination must not require retaining process-local state.
- Reuse canonical node/edge encoding so tags, attributes, kind, text, endpoints,
  relationship, review direction, title, and purpose ID compare exactly.

**Tests and smoke acceptance:**

- Cover identical and reversed comparisons; every add/replace/remove category;
  metadata, tag/attribute, scope-parent, endpoint, relationship, and review-
  direction changes; deterministic order; page boundaries; empty final page;
  invalid/stale cursor; invalid database; and mismatched project.
- Exercise CLI and NDJSON output/error envelopes using temporary application-
  created databases. Prove a second page reconstructs the same ordered detail
  as one larger page.
- Smoke with a temporary copy of the real self-blueprint plus a small reviewed
  target change. Confirm the report explains the intended node/edge meaning,
  excludes unchanged entities, is useful in both directions, and leaves both
  source files byte-for-byte unchanged.

## Ordered backlog

### T16 — reviewed purpose-only project bootstrap

Restrict public initialization to a purpose root with no edges. All first-tree
content must pass ordinary change review and the configured AI gate; opening an
existing verified database remains trusted and causes no provider call. Keep a
private fixture-only populated initializer where tests require it.

### T17 — provider request limits and compact bounded omissions

Measure the exact AI-review payload before dispatch and stop locally above
configured byte/item ceilings with component counts and split/remodel/bypass
guidance. Replace one-record-per-omission output with grouped counts, a small
sample, and fingerprint-bound detail pages. Never auto-partition a write or make
multiple paid review calls.

### T18 — graph observability and dependency-quality reports

Add bounded graph summaries for scope coverage, orphaned/unreachable entities,
review fan-out hotspots, and suspiciously isolated claims. Reports are
diagnostic author aids, not proof and not automatic dependency creation. Add a
deterministic diagram-oriented export only if the reports demonstrate a useful
consumer.

### T19 — ranked lexical retrieval and measured query scaling

Add deterministic explainable ranking: stable ID and exact tag first, then
phrase/token/metadata matches, while preserving literal search. Teach authors to
search aliases, closed-world wording, neighbors, and paths before edits. Measure
the self-blueprint and a larger realistic corpus; avoid loading the complete
graph for bounded reads only where evidence shows material latency or memory
cost. Any SQLite index/schema change requires explicit design reconciliation.

### T20 — evidence-gated semantic retrieval experiment

Pre-record relevant answers and lexical misses for the self-blueprint and one
unrelated corpus. Compare semantic candidates with T19 on recall, false
positives, latency, privacy, offline behavior, cost, index size, and update
invalidation. Ship only with human approval and material measured benefit;
semantic hits remain advisory and never silently expand required review.

### T21 — graph-aware three-way merge and optional Git integration

Using T15’s entity diff, classify compatible stable-ID edits and explicit
conflicts across base/ours/theirs. Never merge SQLite pages. A merged proposal
must enter the normal affected/context review and atomic write path. Evaluate a
read-only Git diff driver after the standalone contract is stable; do not make
Git tooling a runtime requirement.

### T22 — external-artifact drift and renderer adapter evaluation

Evaluate optional artifact anchors containing path plus content hash and a
small versioned adapter contract for generating or checking external documents.
Keep manual/AI propagation from affected anchors as the baseline. Do not turn
arbitrary project text into executable commands or require generated artifacts
to use the core graph.

### T23 — large-import and bulk-authoring workflow

Design bounded, resumable import planning for large initial graphs and
refactors without bypassing review or the atomic final write. Prefer validated
local manifests and scope-sized batches; do not raise AI tool limits blindly or
send a whole project to a provider.

### T24 — domain profile extension contract

Only after real projects reveal repeated needs, define versioned deterministic
profiles for additional schemas, validators, or derived facts. Keep Core domain-
neutral and require explicit migration and compatibility behavior.
