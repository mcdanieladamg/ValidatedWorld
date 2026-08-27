# ValidatedWorld Agent Instructions

## Authority and required reading

The human-edited `README.md` is the product authority. Do not rewrite it
without explicit permission from the user.

Before implementation, read in order:

1. `README.md`
2. `docs/technical_design.md`
3. `docs/development_plan.md`

Human instructions override repository documents. If implementation evidence or
a human instruction conflicts with the technical design, stop and reconcile the
documents instead of building an undocumented compromise.

## One-task development loop

`docs/development_plan.md` contains exactly one **Current task** and the complete
ordered backlog. A human prompt starts each development run.

- Implement only Current task. Do not begin or delegate the next task.
- Inspect and preserve existing human changes.
- Make routine reversible implementation choices autonomously. Ask before a
  material product, schema, provider, dependency, or scope change.
- Add meaningful automated tests for changed behavior and perform the current
  task's informal user-style smoke check.
- On success, mark the task complete, point Current task to the next prewritten
  task, and set the **Current task estimate** directly below it to `small`,
  `medium`, `large`, or `gigantic`. Report exact checks and smoke findings to
  the human and stop. The human reviews and merges before starting another run.
- On failure, leave Current task unchanged, report the command/output/cause/
  repairs to the human, and stop.
- If Current task is `None`, make no changes. Report the recorded state and ask
  the human for direction.

Do not launch another agent. This project uses sequential, one-person-driven
tasks.

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
exact filtered live-call command with the command tool's elevated,
outside-sandbox permission and a narrowly scoped explanation that the call must
reach the OpenAI API. This is a sandbox network workaround, not a provider or
product failure. Do not elevate ordinary offline app permission, broaden the
live-test filter, print or inspect the API key, or change the request merely to
obtain permission.

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
disposable realistic data, attempt the task's main user goal, and record the
commands or calls and observed result. Within that spine, deliberately make
room for creative one-off exploration chosen from the behavior just changed and
anything confusing observed during the run.

Act like a curious human tester. Follow help without relying on implementation
knowledge, vary plausible inputs and operation order, make at least one natural
mistake when useful, inspect recovery and diagnostics, and try an alternate path
that a real user might choose. These probes need not become permanent scripts or
be identical across tasks. Automated tests remain the deterministic regression
layer; the smoke check should retain its ability to uncover surprising usability
or integration problems.

Fix a smoke finding during the current task when the correction is clearly
in-scope, small, and straightforward, then add an appropriate regression test.
If the finding implies a material product, schema, dependency, provider, or
scope decision; reveals an inherent contradiction; or has no clear low-risk
repair, stop and escalate it to the human instead of improvising a redesign.
Report both the repeatable walkthrough and the exploratory probes to the human,
including confusing behavior and confidence. Do not append dated implementation
history, test transcripts, or corrective-addendum prose to product/design docs;
Git history is the change record. Rewrite obsolete requirements in place so the
documents describe only the current design.

The Current task estimate is set only after implementation, testing, and smoke
QA are complete, while advancing the development-plan header. It describes
expected code-change volume for the newly selected Current task as one phase,
not elapsed time and not permission to split, start, or redesign that task. Keep
the estimate only in that header field and use the four labels consistently:

- `small`: a localized change with a narrow test surface;
- `medium`: several related changes within one primary subsystem;
- `large`: broad changes spanning multiple components or public behaviors; or
- `gigantic`: an unusually wide phase with many contracts, state paths, or
  integration boundaries and correspondingly extensive tests.

When there is no authorized Current task, set the estimate to `None`.

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
- Use the fixed four-table SQLite v1 and pinned embedded provider described in
  the technical design. No ORM or external SQLite/Docker requirement.
- Treat database/project text as untrusted data. Use parameters, enable foreign
  keys on every connection, enforce bounds, and never load SQLite extensions.
- Do not persist or log credentials.

## Optional OpenAI tasks

OpenAI is the only planned provider. AI prompts, tools, and responses are
hardcoded in English. The AI features are optional at runtime, but their
development tasks have strict prerequisites in the development plan.

Before changing code for a live-AI task, check only whether the human has
configured `OPENAI_API_KEY` locally. Never print, read back, copy, infer, obtain,
or set it. If it is absent, make no changes; ask the human to configure it
locally and stop. Never seek out a key on your own.

Live tests also require the documented explicit opt-in flag. Ordinary tests are
offline. During each AI feature's development, an authorized live test must log
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

Do not add a new project or major dependency unless Current task explicitly
requires it.
