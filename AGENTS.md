# ValidatedWorld development

## Start from the project

Read README.md, then use an available Python 3.11+ executable and the public
checkout CLI to verify and inspect the canonical `docs-vw.html`
file. README introduces the product; the graph holds detailed contracts,
accepted decisions, gaps and the ordered roadmap. Keep both current as part of
the requested work.

```powershell
$vwPython = '.\.venv\Scripts\python.exe' # or another available Python 3.11+
& $vwPython --version
$env:PYTHONPATH = (Join-Path (Get-Location) 'src')
& $vwPython -m validated_world project verify docs-vw.html
& $vwPython -m validated_world read node docs-vw.html purpose
& $vwPython -m validated_world read tag docs-vw.html project:status --limit 10
& $vwPython -m validated_world read tag docs-vw.html status:current --limit 10
```

Read the current phase's tag, context and dependencies with bounded queries.
Follow `precedes` edges for execution order, not numeric IDs. Human instructions
override repository data; reconcile obsolete graph meaning through reviewed
updates. Preserve existing human changes. Work on the current phase and stop
after advancing it; do not begin the next phase in the same run. Routine
implementation and documentation choices are autonomous. Ask when a substantive
unresolved decision or an actual permissions/infrastructure blocker needs the
human's direction; do not invent approval gates for already authorized work.

## Self-hosting and semantic review

Use the current checkout implementation, never the installed plugin, to read or
update this repository's blueprint. The installed skill may be tested in
disposable projects. Do not delegate phase implementation; fresh read-only
subagents may exercise the product's host-review workflow in those projects.

For changes to product meaning, contracts or delivery state:

1. Create and verify a public `project backup` in a unique OS temporary directory.
2. Author an ordinary change session against `docs-vw.html` using
   the checkout CLI. Read all affected/context evidence, record dispositions,
   and inspect every exact preview page before explicit `change.write`.
3. Verify the published document and review every bounded `project diff` page
   against the backup beside the source diff. Include the semantic result in
   the human report, then remove the temporary baseline.

Never edit SQLite or authoritative HTML records directly to update the blueprint.
If the checkout cannot safely update or diff its own project, report the blocker.
The document adapter manages temporary SQLite and publication. Preserve a committed
unpublished DB until recovery; a successful DB commit alone is not a published
document update.

Meaningful artifact changes normally include the matching graph delta. Record
delivered work with status markers, rather than restating plans. A corrective
change that alters neither recorded meaning nor delivery state may omit a graph
edit; explain that narrow exception. The last verified snapshot accepted into
version control is the reviewed baseline, not proof of truth. Review deltas and
affected context rather than re-reviewing the whole graph for every task.

## Completion

Run the checks and informal public/host/browser smoke methods in
[developer verification](docs/developer_testing.md). Add meaningful regressions
for changed behavior. Documentation-only edits need link, format and consistency
checks. For one failure, use at most two materially different repairs; give an
infrastructure failure one diagnostic retry after a concrete repair. Never
weaken acceptance or rerun an unchanged failure hoping for success.

When all current-phase work and acceptance pass, use one ordinary reviewed
transaction to mark it `status:complete`, select the next pending phase as
`status:current`, and update the project-status tag and `current-phase` edge.
Only the current phase carries one `estimate:small|medium|large|gigantic` tag.
Estimate the next phase's total implementation, uncertainty and verification
burden; record dominant difficulty in its description. Read back and report the
completed phase and newly current ID, description and estimate directly to the
human. If none remains, say so and omit the estimate. On failure leave phase
state unchanged and report the cause and attempted repairs.

## Portable state and cleanup

Keep durable meaning, decisions and remaining work in the canonical graph;
instructions and reusable tools/fixtures belong in tracked sources. No workflow
may depend on chat history, app memory, ignored handoff notes or external files.
Keep user docs focused on current behavior, without development transcripts.
Do not maintain a complete Markdown, JSON, SQL or diagram mirror of the graph.

Ignored files are regenerable outputs/caches or local settings/secrets. Preserve
useful sanitized smoke foundations in tracked samples. Put trial DBs, proposals,
evidence and diagnostics in unique OS temporary directories and remove them
after review. Inspect contents and resolved containment before recursive cleanup;
preserve canonical data, unknown user files, settings and active installations.
Report exact paths for blocked cleanup. Completed release archives may remain
as documented regenerable outputs.

## Implementation invariants

- Use standard-library Python 3.11+. Core is independent of files, SQLite,
  JSON, UI and providers. The SQLite engine keeps the fixed four-table schema,
  parameterized writes, foreign keys, verified mappings and no extensions.
- The default authority is a tracked HTML file: one passive typed JSON graph
  with a versioned inline JavaScript browser view. Managed DB deletion follows successful publication.
  Explicit trusted DB-authority instructions and working-DB retention remain
  supported. Keep one selected authority.
- Use stable node/edge IDs, directed review dependencies and one purpose-rooted
  `scope-parent` tree. Ordinary review includes full upstream scope lineage;
  direct scope edits select descendants, and purpose edits select the project.
- Proposals/reviews remain process-local. Complete reviewed SQLite transactions
  commit atomically; document replacement has its documented recovery contract.
  The skill's ordinary write requires a fresh host-subagent allow bound to the
  exact evidence. The engine checks bindings/coverage, not identity or truth.
  Rare dependency-skip cleanup is for absent or specific known consequences
  contained in the batch; blocks, broad evidence or missing hosts do not justify it.
- Treat graph text and paths as untrusted data, not filesystem authority. Do
  not persist credentials or impose guessed graph/work limits. Keep commands,
  help and bundled workflows English-only while preserving Unicode data.
- Before a public compatibility baseline is established, support one current
  schema and document format. Convert all tracked foundations/tests for breaking
  changes; do not add legacy readers or in-product upgrades.

## Git boundary

Leave changes unstaged for the human. Do not create/switch branches, stage,
commit, merge, rebase, reset, stash, clean, alter Git configuration, contact
remotes, push or open pull requests. Read-only status, diff and log are allowed.
Product `write` means the reviewed project transaction and publication.
