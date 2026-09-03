# ValidatedWorld Development Plan

**Current task:** T16 — reviewed purpose-only project bootstrap

**Current task estimate:** medium

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
| T15 | complete | Bounded semantic database diff |
| T16 | current | Reviewed purpose-only project bootstrap |
| T17 | pending | Provider request limits and compact bounded omissions |
| T18 | pending | Graph observability and dependency-quality reports |
| T19 | pending | Ranked lexical retrieval and measured query scaling |
| T20 | pending | Evidence-gated semantic retrieval experiment |
| T21 | pending | Graph-aware three-way merge and optional Git integration |
| T22 | pending | External-artifact drift and renderer adapter evaluation |
| T23 | pending | Large-import and bulk-authoring workflow |
| T24 | pending | Domain profile extension contract |

## Ordered backlog

### T16 — reviewed purpose-only project bootstrap

**Goal:** A new canonical world cannot acquire populated content outside the
ordinary review/write path.

**Implement:**

- Make every public initializer accept only project metadata plus one purpose
  node and no edges. Reject a populated graph before creating a database, with
  guidance to use a change session for all later content.
- Give one-shot CLI and NDJSON the same purpose-only contract. Keep complete-
  graph initialization private to built-in samples and test fixtures rather
  than exposing a second public trust path.
- Preserve existing-project behavior: opening a structurally valid,
  fingerprint-correct `.vw.db` trusts its committed state and never invokes AI
  review merely because it was opened.
- The first node/edge addition uses ordinary projection, affected/context
  review, optional AI gate, stale checks, and atomic write. The explicit single-
  write bypass continues to waive only AI review.

**Tests and smoke acceptance:**

- Purpose-only creation succeeds through CLI and NDJSON; populated public
  initialization fails without leaving a canonical or temporary database.
- A first real addition cannot write with pending manual review and reaches an
  enabled deterministic reviewer double only after manual readiness. Existing-
  project open reaches no provider.
- Built-in samples and existing test fixtures still initialize through the
  private trusted path. Smoke a new project from purpose creation through one
  reviewed child/scope-edge commit and reopen the verified result.

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

Using the semantic entity diff, classify compatible stable-ID edits and explicit
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
