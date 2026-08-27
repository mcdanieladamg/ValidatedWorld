# ValidatedWorld Development Plan

**Current task:** T13 — Optional OpenAI authoring agent

**Current task estimate:** gigantic

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

Ordinary tests are offline. Live OpenAI development checks require the local
key and explicit `LiveTests` flag described in technical design section 8. Log
and inspect the exact credential-free serialized request, use the smallest
meaningful known/control cases, and make no automatic paid retries, parallel
paid calls, or fallback-model calls. At any provider trouble—including quota,
authentication, transport, timeout, refusal, malformed output, or exhausted
credit—stop paid calls immediately, report the non-secret failure, and ask the
human for feedback.

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
| T9 | complete | Complete CLI/NDJSON manual workflow |
| T10 | complete | Realistic MVP scenarios and usability hardening |
| T11 | complete | MVP release decision |
| T12 | complete | Automatic OpenAI semantic write gate |
| T13 | pending | OpenAI authoring agent |
| T14 | optional | Evaluate an OpenAI plugin format with the human |

## T13 — optional OpenAI authoring agent

**Prerequisites:** T12 complete; T13 is current; a human-configured local
`OPENAI_API_KEY`; and the explicit authoring live-test flag. If the key is
absent, make no code changes and ask the human to configure it locally. Never
set or display it.

**Read:** README AI-first flow and technical design section 8.

**Goal:** Add optional conversational English authoring through the same bounded
Application use cases while preserving exact human approval, the automatic
independent semantic write gate, and complete manual use.

**Implement:**

- Strict read/change tools with bounded context, tool-call, search, traversal,
  and operation limits; no raw SQL or direct canonical writes.
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

## T14 — optional plugin-format evaluation

Discuss feasibility and value with the human. Implement only if the human makes
T14 current with a concrete requested scope.
