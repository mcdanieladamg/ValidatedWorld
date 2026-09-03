# ValidatedWorld Development Plan

**Current task:** T15 — safe new-world bootstrap and tracked self-blueprint scenario

**Current task estimate:** large

This file is the current implementation checklist for humans and coding agents.
It contains one current task and the complete ordered backlog. The README is the
product authority, and [technical_design.md](technical_design.md) defines the
current technical contract. Git history and task reports record past work; do
not append dated implementation narratives or corrective addenda here.

## Development loop

Before implementation:

1. Read `README.md`, `AGENTS.md`, `docs/technical_design.md`, and this file.
2. Inspect the current source, tests, and working-tree changes.
3. Implement only Current task. Do not begin or delegate the next task.
4. Reconcile any human instruction or implementation evidence that conflicts
   with the documented design before changing dependent code.

On success:

1. Add meaningful automated tests and perform an informal public-surface smoke
   check with disposable realistic data.
2. Run the required completion sequence below.
3. Mark only the completed task in the progress table.
4. Point Current task to the next prewritten task and set its estimate to
   `small`, `medium`, `large`, or `gigantic`; use `None` when no task is
   authorized.
5. Report implementation, exact checks, smoke findings, uncertainty, and the
   new Current task to the human, then stop.

On failure, leave Current task unchanged, report the command, non-secret output,
cause, and repairs tried, then stop. Do not preserve a troubleshooting transcript
in product or design documentation.

## Testing and bounded repair

Use focused tests while developing. When ready, run sequentially:

```powershell
dotnet restore ValidatedWorld.slnx
dotnet build ValidatedWorld.slnx --no-restore
dotnet test ValidatedWorld.slnx --no-build --no-restore
```

If restore cannot read the user-level `NuGet.Config`, follow the exact elevated
permission workaround in `AGENTS.md`; do not inspect or modify that file. Make
at most two materially different repairs for one failure, never weaken an
acceptance criterion, and give infrastructure failures only one diagnostic
retry after a concrete repair.

The smoke check enters through the public surface, follows public help, uses a
disposable application-created database, attempts the main user goal, makes one
natural mistake when useful, and tries a plausible alternate path. Report the
walkthrough and findings to the human rather than adding a dated evidence
section here.

The normal full-solution test command discovers offline and live tests together.
A live test calls OpenAI only when its feature has an effective key, is enabled,
and has its explicit `LiveTests` flag set; otherwise it completes without a
provider call. Do not override those effective settings in the test command.
For an enabled live check, log and inspect the exact credential-free serialized
request and provider response, use the smallest meaningful known/control cases,
and make no automatic paid retries, parallel paid calls, or fallback-model
calls. At provider trouble, follow the human's current retry instructions and
the sandbox network workaround in `AGENTS.md`; absent a human override, stop
paid calls immediately and report the non-secret failure.

## Repository guardrails

- Preserve unrelated human changes.
- Do not create/switch branches or stage, commit, merge, rebase, cherry-pick,
  revert, reset, clean, stash, alter Git configuration, contact remotes, push,
  or open pull requests.
- Do not launch another agent.
- Do not add a project, major dependency, persistence mechanism, provider, UI,
  or domain feature unless Current task requires it.
- Never search for, expose, copy, or configure credentials.
- “Write” and “apply” in product prose mean the ValidatedWorld transaction, not
  a Git operation.

## Progress

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
| T9 | complete | Filesystem-like stateful shell and structured CLI/NDJSON workflows |
| T10 | complete | Realistic MVP scenarios and usability hardening |
| T11 | complete | MVP release decision |
| T12 | complete | Automatic OpenAI semantic write gate |
| T13 | complete | OpenAI authoring agent |
| T14 | complete | Plugin format evaluated within T13; no MVP plugin retained |
| T15 | current | Safe new-world bootstrap and tracked self-blueprint scenario |
| T16 | pending | Provider request-size preflight and compact bounded omissions |
| T17 | pending | Author discovery guidance and ranked lexical retrieval |
| T18 | pending | Evidence-gated semantic retrieval experiment |

## T13 — optional OpenAI authoring agent

**Prerequisites:** T12 complete; T13 is current; a human-configured local key
reported by the README's non-secret `ai.status` check; and the explicit
authoring live-test flag. If effective key configuration is absent, make no code
changes and ask the human to configure it locally. Never set or display it.

**Read:** README AI-first flow and technical design section 8.

**Goal:** Add optional conversational English authoring through the same bounded
Application use cases while preserving exact human approval, the automatic
independent semantic write gate, and complete manual use.

**Implement:**

- Strict read/change tools with bounded context, tool-call, search, traversal,
  and operation limits; no raw SQL or direct canonical writes. Prefer compact
  incremental entity operations over complete-batch retransmission, and omit
  complete proposed graphs and accumulated batches from iterative tool results.
- Search-before-create behavior, material clarification questions, one
  process-local change session, affected/review iteration, and new/existing
  project previews.
- Exact short-lived human approval bound to database/project, conversation/
  session, expiry, and all base/operation/proposed/affected/context/review
  fingerprints. Any change invalidates approval.
- A guarded normal write tool that never sets `bypassAiReview`. When semantic
  review is configured and enabled, `change.write` automatically obtains or
  reuses the independent `allow`/`block` decision. The authoring agent may repair
  or discuss a block but cannot create, override, or dismiss that decision.
- Disabled/unconfigured fallback, one OpenAI Responses client, and zero
  automatic paid retries or fallback providers/models.
- English authoring instructions that teach stable broad scope containers and
  IDs, focused volatile claims, source-of-truth-to-consumer direction,
  fan-in/fan-out roster hubs rather than sibling cliques, useful artifact-level
  anchors, and `both` only for genuine mutual reconsideration.
- Before changing counts, complete lists, canonical names, or aliases, search
  closed-world wording and propose missing semantic edges when warranted. Ask
  when the set/reference is materially ambiguous. Treat unexpectedly tiny or
  huge affected previews as a reason to inspect the model.
- Reuse deliberate namespaced tags after exact-tag lookup; explain that tags do
  not create dependencies or executable conditions. Use attributes for named
  scalar values and explicit edges for stale-if-changed relationships. Never
  tag-filter required affected/context data.

**Tests and acceptance:**

- Small offline scripted tests cover bounded search, duplicate/unrelated
  avoidance, material questions, session loss, review iteration, stale approval,
  no SQL/direct write/automatic disposition, no AI-review bypass, independent
  block repair, and disabled/unconfigured manual fallback.
- An explicitly enabled minimal live run logs and inspects exact prompts,
  context, and tool schemas without credentials. Use the smallest practical
  new-project and existing-project cases to evaluate correctness, user burden,
  unrelated mutations, questions, affected coverage, approval binding, reviewer
  block/repair flow, tokens, cost, and latency.
- Exercise a compact lore progression covering a sixth roster member,
  roster/count reconciliation, canonical rename, local detail, broad scope
  change, deletion, and reparent. Assert roster hubs rather than sibling cliques,
  stable IDs, semantic consumers, old/new scope lineages, visible topology
  changes, and exclusion of unrelated local lore.
- Retain the authoring feature only if it meaningfully reduces burden without
  bypassing the README’s review/write guarantees.

## T14 — plugin-format evaluation

Evaluate feasibility and value with the human. The authorized evaluation was
absorbed into T13. The current plugin format can bundle a local stdio MCP
server, but the MVP retains `ai-assistant-shell`: it keeps the portable database
at the user-selected path, preserves one trusted process for the in-memory
session and direct human approval, and avoids packaging a source-relative server
before ValidatedWorld has a distributable local executable. Any future plugin
must reuse the bounded tool contract without weakening those guarantees.

## T15 — safe new-world bootstrap and tracked self-blueprint scenario

**Goal:** Close the initialization trust bypass and make ValidatedWorld's own
architecture a repeatable, tracked, realistic use case.

**Implement:**

- Restrict every public new-project entry point to one purpose node and no
  edges. Reject attempts to establish a populated canonical graph through
  `project.init` with an actionable message directing the caller to change
  sessions.
- Keep existing `.vw.db` open behavior: verify structure and fingerprints, then
  assume the committed world was already reviewed. Do not call AIReview merely
  because a database is opened.
- If tests or built-in sample creation need complete graph loading, keep it an
  internal trusted-fixture path that is unavailable to CLI/NDJSON authoring.
- Retain `samples/ValidatedWorldBlueprint/baseline.json` as the readable source
  for the semantic blueprint. Add a repeatable scenario that starts with the
  purpose root, adds the graph in stable scope-sized batches through ordinary
  change sessions, reviews the affected/context slice, and commits each batch.
- Do not use the authoring model to build the sample. Do not submit the complete
  blueprint or complete project to AIReview. Provider-enabled smoke coverage is
  limited to one representative small change; deterministic reviewer doubles
  cover the complete piecewise replay without paid calls.
- Exercise user questions against the resulting database: find the write path,
  locate a feature and its risks, inspect one dependency path, make one local
  correction, and verify unrelated scope branches stay out of the affected set.

**Tests and acceptance:**

- Public purpose-only creation succeeds; public populated initialization fails
  before SQLite establishes canonical content.
- The first non-root addition follows normal validation, manual review, and the
  enabled reviewer gate. Existing-project open performs no provider call.
- Explicit single-write bypass remains available and still requires all
  deterministic/manual readiness.
- The generated blueprint verifies, matches the tracked source counts and
  fingerprint, and the user-style queries above return useful bounded results.

## T16 — provider request-size preflight and compact bounded omissions

**Goal:** Make both sides of a bounded interaction remain bounded without
inventing automatic multi-call review.

**Implement:**

- Serialize the exact semantic-review request before provider dispatch and
  enforce configurable byte and item ceilings. On failure, do not call the
  provider; return totals grouped by operations, affected nodes, edges, paths,
  scope context, and manifest data, plus guidance to split the change, improve
  graph modeling, or use the explicit bypass.
- Replace one-record-per-omission responses with counts grouped by omission
  reason, a small deterministic sample, and a stable cursor/detail query for
  retrieving additional omitted identities and evidence in bounded pages.
- Apply the same response accounting to CLI, NDJSON, and authoring tools. An
  item limit includes omission metadata rather than counting only primary rows.
- Do not automatically partition a proposal, make parallel reviewer calls, add
  multiplayer behavior, or send a whole project in one request or many pieces.

**Tests and acceptance:**

- An over-budget review reaches no provider and identifies which components
  caused the limit; an unchanged in-budget request remains fingerprint-bound.
- Large omitted sets produce constant-size first responses, deterministic
  pages recover exact details, and required data is never reported as complete
  when it was omitted.

## T17 — author discovery guidance and ranked lexical retrieval

**Goal:** Reduce missed relevant nodes without pretending retrieval can infer
all dependencies.

**Implement:**

- Strengthen author instructions to search several terms, aliases, canonical
  names, and closed-world wording before edits; inspect incoming/outgoing review
  neighbors and relevant paths when affected results look unexpectedly small.
- Expose concise bounded neighbor/dependency reads to the author if the current
  tools cannot answer those questions directly.
- Add deterministic ranked lexical search with stable pagination and match
  explanations. Prefer exact stable ID and exact tag, then phrase, token
  coverage, kind, and metadata matches. Preserve the existing literal search.
- Start without embeddings. Use in-memory ranking unless measurements justify
  a SQLite search index; adding FTS or another dependency requires the normal
  schema/dependency decision.

**Tests and acceptance:**

- Domain synonyms supplied as aliases/tags and multi-token queries rank useful
  nodes ahead of incidental substring hits with a visible reason.
- Search remains bounded and deterministic. Candidate discovery never creates
  dependency edges, broadens required review silently, or claims completeness.

## T18 — evidence-gated semantic retrieval experiment

**Goal:** Decide whether semantic similarity materially improves discovery over
ranked lexical search on real semantic graphs.

**Implement:**

- Evaluate the self-blueprint and one unrelated realistic corpus using queries
  whose relevant nodes and lexical misses are recorded in advance.
- Compare retrieval quality, latency, privacy, provider cost, index size,
  invalidation on node updates, and offline behavior against T17.
- Implement semantic retrieval only if the measured benefit is material and the
  human approves any provider, model, schema, or dependency choice. Otherwise
  record the rejection in the current design and close the task.
- Treat semantic results as advisory candidates. They never become canonical
  edges, proof of consistency, or mandatory affected nodes without review.

**Tests and acceptance:**

- The evaluation is reproducible and reports both useful discoveries and false
  positives. The task may validly conclude with no semantic feature.
