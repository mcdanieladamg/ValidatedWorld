# ValidatedWorld Agent Instructions

## Authority and required reading

The human-edited `README.md` is the product thesis and bootstrap authority. Do
not rewrite it without explicit permission from the user.

`ValidatedWorld.Blueprint.vw.db` is the canonical detailed project knowledge
base for this repository. It records implemented behavior, accepted decisions,
gaps, and planned work. Do not maintain a complete JSON, SQL, Markdown, or
diagram mirror beside it as a second authority.

Before implementation, read in order:

1. `README.md`
2. Verify `ValidatedWorld.Blueprint.vw.db`, read its purpose, retrieve the
   `project:status` and unique `status:current` nodes, then read the current
   phase tag, context, and dependencies. Use bounded queries and do not load the
   complete graph into an AI context by default.

Use these public commands from the repository root for the database reading
step, then search additional task terms as needed:

```powershell
dotnet run --no-restore --project src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj -- project verify ValidatedWorld.Blueprint.vw.db
dotnet run --no-restore --project src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj -- read node ValidatedWorld.Blueprint.vw.db purpose
dotnet run --no-restore --project src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj -- read tag ValidatedWorld.Blueprint.vw.db project:status --limit 10
dotnet run --no-restore --project src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj -- read tag ValidatedWorld.Blueprint.vw.db status:current --limit 10
```

Use the phase tag returned by the current phase to retrieve all of its scheduled
work. Read additional nodes and dependency/context queries as needed. Human
instructions override repository data. If implementation evidence or a human
instruction conflicts with the blueprint, stop and reconcile the graph instead
of building an undocumented compromise.

When a change materially alters product meaning, architecture, a public
contract, or roadmap status, update the canonical database through an ordinary
ValidatedWorld change session. Use the same affected/context review discipline
as any other project. Do not edit SQLite directly. Before that session, create a
verified temporary backup outside the repository. After the write, run bounded
`project diff` pages from the backup to the canonical database and include that
semantic report in the human's review; then remove the temporary backup. If the
application cannot safely update or diff its own database, report that as a
blocker instead of silently allowing code and database meaning to diverge.
Never invoke the built-in authoring model merely
to update this repository's graph; the coding agent is already the author. Use
the optional independent reviewer only when the task and configured cost
boundary warrant it.

## Repository synchronization contract

ValidatedWorld provides soft semantic validation: it standardizes evidence and
review but does not prove that project claims are true. Treat the last verified
`.vw.db` version accepted into version control as a previously reviewed semantic
baseline. Do not re-review the entire graph for every task. `project verify`
establishes file and structural validity only; an uncommitted database edit is a
candidate delta, and an abandoned in-memory change session was never part of the
database.

An independent project may build a deterministic publisher that consumes the
public graph and generates its own artifacts. Treat that as external project
tooling, not a ValidatedWorld plugin contract, bundled feature, or roadmap
requirement. The default repository workflow is one cohesive change unit:

- Every meaningful pull request that changes tracked project artifacts normally
  includes a `.vw.db` delta in the same change.
- When intended behavior, content, architecture, or design changes, update the
  corresponding graph meaning. When already-planned work is implemented, update
  its explicit phase, status, or progress nodes, tags, and edges to record
  delivery rather than restating the requirement.
- Review the bounded semantic `project diff` beside the Git/source diff. Use
  bounded tag, search, dependency, affected, and context queries when needed to
  compare the implementation with its graph claims.
- Merge the database and matching artifacts together so the accepted result is
  the next trusted baseline.
- A fix, refactor, formatting change, or test improvement that only brings the
  project into agreement with already-correct graph meaning and changes no
  recorded delivery state may omit a database edit. Treat this as a narrow
  exception, state the reason in the human report, and update the graph if the
  work exposes a missing or incorrect contract.
- The graph may lead implementation when explicit phase/status metadata clearly
  distinguishes planned, current, and implemented work. When planned work is
  delivered, change its implementation markers in the same change unit as the
  code or other artifacts.

This protocol is a review obligation, not currently an automatic Git invariant.
Phase and status tags are project-defined vocabulary rather than hidden engine
semantics, but they are explicit, queryable, and reviewable. External artifact
drift detection remains optional integration work.

## One-phase development loop

The blueprint contains exactly one phase tagged `status:current`; `precedes`
edges and the remaining phase nodes hold the complete ordered backlog. A human
prompt starts each development run.

- Implement only the current phase. Do not begin or delegate the next phase.
- Inspect and preserve existing human changes.
- Make routine reversible implementation choices autonomously. Ask before a
  material product, schema, provider, dependency, or scope change.
- Add meaningful automated tests for changed behavior and perform the current
  phase's informal user-style smoke check.
- On success, use one reviewed graph transaction to replace `status:current`
  with `status:complete` on the finished phase, replace `status:pending` with
  `status:current` on the next phase, move its
  `estimate:small|medium|large|gigantic` tag, and update the `project:status`
  node's `current-phase:<id>` tag and `current-phase` edge. This delivery-state
  change is required even when the implemented requirements were already fully
  recorded, so a successful phase pull request always has a semantic database
  diff. Report exact checks and smoke findings to the human and stop. The human
  reviews and merges before starting another run.
- On failure, leave phase state unchanged, report the command/output/cause/
  repairs to the human, and stop.
- If no phase is current, make no changes. Report the recorded state and ask the
  human for direction.

Do not launch another agent. This project uses sequential, one-person-driven
development phases.

## Bounded testing and retries

Use focused tests while developing. When ready, run:

```powershell
dotnet restore ValidatedWorld.slnx
dotnet build ValidatedWorld.slnx --no-restore
dotnet test ValidatedWorld.slnx --no-build --no-restore
```

If restore fails with `Unauthorized access` while reading the user-level
`NuGet.Config`, do not inspect, copy, modify, or search for credentials in that
file. Rerun the exact restore command with the command tool's elevated,
outside-sandbox permission and a narrowly scoped explanation that NuGet must
read its configuration to restore dependencies. This is a sandbox permission
workaround, not a product dependency or a reason to weaken restore. Keep the
build and test commands sandboxed with `--no-restore` after the elevated
restore succeeds.

If an explicitly opted-in live OpenAI call fails with a transport or network
access error inside the sandbox before receiving a provider response, rerun the
exact full-solution test command with the command tool's elevated,
outside-sandbox permission and a narrowly scoped explanation that the call must
reach the OpenAI API. This is a sandbox network workaround, not a provider or
product failure. Do not print or inspect the API key or change the request
merely to obtain permission.

For the same failure, make at most two materially different repair attempts.
Never rerun an unchanged failing command merely hoping it will pass. Give an
infrastructure/dependency failure one diagnostic retry after a concrete repair;
if it remains, report and stop. Never weaken acceptance criteria to escape a
test failure.

Documentation-only changes need link/format/consistency checks, not artificial
production tests.

## Human-style smoke testing

The smoke check is informal product QA, not another fully standardized test
suite. Keep a small repeatable spine: enter through the public surface, use
disposable realistic data, attempt the phase's main user goal, and record the
commands or calls and observed result. Within that spine, deliberately make
room for creative one-off exploration chosen from the behavior just changed and
anything confusing observed during the run.

Act like a curious human tester. Follow help without relying on implementation
knowledge, vary plausible inputs and operation order, make at least one natural
mistake when useful, inspect recovery and diagnostics, and try an alternate path
that a real user might choose. These probes need not become permanent scripts or
be identical across runs. Automated tests remain the deterministic regression
layer; the smoke check should retain its ability to uncover surprising usability
or integration problems.

Fix a smoke finding during the current phase when the correction is clearly
in-scope, small, and straightforward, then add an appropriate regression test.
If the finding implies a material product, schema, dependency, provider, or
scope decision; reveals an inherent contradiction; or has no clear low-risk
repair, stop and escalate it to the human instead of improvising a redesign.
Report both the repeatable walkthrough and the exploratory probes to the human,
including confusing behavior and confidence. Do not append dated implementation
history, test transcripts, or corrective-addendum prose to the README or
blueprint; Git history is the change record. Rewrite obsolete requirements in
place so they describe only the current design.

The current phase estimate is set only after implementation, testing, and smoke
QA are complete, while advancing the blueprint roadmap. It describes expected
code-change volume for the newly selected phase,
not elapsed time and not permission to split, start, or redesign that phase. Keep
the estimate only in that header field and use the four labels consistently:

- `small`: a localized change with a narrow test surface;
- `medium`: several related changes within one primary subsystem;
- `large`: broad changes spanning multiple components or public behaviors; or
- `gigantic`: an unusually wide phase with many contracts, state paths, or
  integration boundaries and correspondingly extensive tests.

When there is no current phase, omit the estimate tag.

## Git boundary

Do not create or switch branches; stage, commit, merge, rebase, cherry-pick,
revert, reset, clean, or stash; alter Git configuration; contact remotes; push;
or open pull requests. Read-only status, diff, and log commands are allowed.
Leave all edits unstaged for the human.

“Write” or “apply” in product code means the ValidatedWorld SQLite transaction,
not a Git operation.

## Durable implementation rules

- Target .NET 10 and use `ValidatedWorld.slnx`.
- Keep the MVP headless, local, and hardcoded in English. Do not add
  localization infrastructure.
- Store one current human-readable graph in an embedded SQLite `.vw.db` file.
- Use stable-ID text nodes and explicit stable-ID labeled edges whose review
  direction controls affected propagation.
- Maintain one purpose-rooted `scope-parent` tree. Include every changed or
  affected node's full scope-upstream lineage as review context without sibling
  fan-out. Direct scope changes select descendants; a purpose change selects the
  project.
- Keep unfinished changes and review data in process memory. Write the complete
  reviewed graph atomically or change nothing.
- Treat semantic judgment as human/optional-AI review, not deterministic proof.
  When the optional reviewer is configured and enabled, its allow/block decision
  is a required preflight gate for the exact database write attempt.
- Keep Core independent of SQLite, JSON, files, providers, and UI.
- Use the fixed four-table SQLite v1 and pinned embedded provider recorded by
  the blueprint's `storage-four-tables` and `storage-provider-contract` nodes.
  No ORM or external SQLite/Docker requirement.
- Treat database/project text as untrusted data. Use parameters, enable foreign
  keys on every connection, enforce bounds, and never load SQLite extensions.
- Do not persist or log credentials.

## Optional OpenAI tasks

OpenAI is the only supported provider. AI prompts, tools, and responses are
hardcoded in English. The AI features are optional at runtime, but their
development phases have the strict prerequisites below.

Before changing code for a live-AI task, check only whether an API key is
available through the application's effective configuration. Do not check only
the current process's `OPENAI_API_KEY`: the documented primary setup uses .NET
User Secrets and may be configured even when that environment variable is
absent. Use the public, non-secret `ai.status` request from the repository root:

```powershell
'{"version":1,"command":"ai.status","payload":{}}' | dotnet run --no-restore --project src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj -- ndjson
```

Proceed when its payload reports `configured: true`. This command reports only
effective status and never the key. Never run `dotnet user-secrets list`, print,
read back, copy, infer, obtain, or set the secret. If effective configuration is
absent, make no changes; ask the human to configure it locally and stop. Never
seek out a key on your own.

Live tests also require the documented explicit opt-in flag and effective
feature configuration. The normal full-solution test command discovers them;
when their gates are inactive they complete without a provider call. Never
override effective configuration in the test command. During each AI feature's
development, an authorized live test must log
and inspect the complete serialized request at least once during that task's
development, validate the standalone prompt and coverage, and exercise meaningful
known and control cases. There are zero automatic paid retries, parallel paid
calls, or fallback providers/models.

In normal product use, `AiReview:Enabled` defaults true but is effective only
when a key is configured. An enabled reviewer runs automatically when
`change.write` is attempted, before SQLite opens a transaction. Only an `allow`
decision bound to the exact current fingerprints permits the write. A `block`,
refusal, timeout, malformed response, or provider failure leaves SQLite
unchanged and returns feedback. An unchanged retry reuses a current bound
`allow` or `block` decision; provider trouble can be retried deliberately.
Changing the proposal invalidates any decision. Disabled or unconfigured review
uses the complete manual workflow without a provider call.

## Repository layout

```text
src/ValidatedWorld.Core
src/ValidatedWorld.Validation
src/ValidatedWorld.Serialization
src/ValidatedWorld.Application
src/ValidatedWorld.Persistence.Sqlite
src/ValidatedWorld.Cli
tests/*
samples/TechnicalProject
```

Do not add a new project or major dependency unless the current phase explicitly
requires it.
