# ValidatedWorld technical guide

ValidatedWorld keeps a project model in one local SQLite `.vw.db` file. Nodes
hold project claims, decisions, evidence, scope, status, and uncertainty. Edges
make scope and review consequences explicit. The system provides deterministic
structure and review evidence; it does not prove that a claim is true or infer
missing dependencies.

## Graph model

Every project has one purpose node and one `scope-parent` tree rooted at that
purpose. Each non-purpose node has exactly one scope parent. Other edges may
carry a review direction so a change can identify connected claims that need
independent agent review.

Node and edge IDs are stable text identifiers. Tags are sorted unique strings.
Attributes are sorted unique name/value pairs with six value kinds: text,
signed 64-bit integer, canonical decimal text, Boolean, symbol, and canonical
UTC instant. Unicode graph text is stored and round-tripped, while the product
workflow and diagnostics are supported in English.

## Storage and verification

The Python provider uses the standard-library `sqlite3` module and a fixed
four-table schema for schema metadata, project metadata, nodes, and edges. It enables
foreign keys on every connection, does not load SQLite extensions, uses
parameterized writes, and performs schema, integrity, foreign-key, structural,
and state-fingerprint checks when opening a project. Active graph-rule results
are reported separately so an invalid baseline can still be opened and repaired;
reviewed writes require the proposed graph to pass them.

Project initialization, backup, and reviewed writes never overwrite a requested
destination. A write checks the base fingerprint before and after acquiring its
SQLite write transaction, applies the complete reviewed operation batch, and
either commits the resulting graph or rolls back.

## Review workflow

Change sessions live only in one `ndjson` process. The session records an exact
base snapshot, normalized operations, the proposed graph, structural and rule
validation, affected nodes, scope context, review dispositions, preview state,
and any host-subagent decision submitted for the skill workflow.

An ordinary write requires all of the following:

- a structurally valid proposed graph;
- valid active graph rules;
- a disposition for every affected node;
- presentation of every required scope-context node;
- presentation of every page of the exact proposal preview;
- an unchanged base database fingerprint.

The ordinary skill workflow additionally calls `change.agent-review` with a fresh host
subagent's decision, then `change.agent-write`. It requires a current `allow`
bound to the exact proposal fingerprints. The agent then saves and reports the
result without a routine additional end-user approval step. It asks for clarification
when intent or scope is uncertain and honors requests for extra content review.
When a decision warrants approval, it summarizes intended changes in natural language.

For rare cleanup of a poorly modeled graph, the main agent may select one proposal
to skip dependency review when there are no downstream consequences, or only
specific already-known consequences handled by edits in the same batch. All node and edge edits stay in
one atomic transaction, and its preview shows only their before/after values.
The engine does not traverse consequences or collect upstream scope context,
request dispositions, or require an independent reviewer for that proposal.
Structural validity, active graph rules and stale-write protection still apply.
The bypass resets on the next apply, patch, expand, or session; normal review
remains the default. Skipping is highly discouraged for routine updates. Uncertain
impact needs normal review; broad review, a blocked decision or missing host
capabilities alone does not justify skipping. Selection belongs to the main agent
and requires no separate end-user authorization.

Any changed operation or review state invalidates older references. EOF,
disconnect, cancellation, or explicit discard loses the in-memory proposal and
does not alter the database.

## Rules and views

Rules are normal graph nodes with kind `validation-rule`, tag `rule:active`, a
`rule:version` integer attribute, and a `rule:expression` text attribute. Named
views use kind `validation-view` plus `view:name`, `view:version`, and
`view:expression` attributes. Expressions are JSON and evaluate against the
complete candidate graph.

The rule language supports node and edge selectors, named views, set union,
intersection and difference, reachability, Boolean composition, existence and
count checks, subsets and equal sets, universal tag conditions, acyclic and
single-chain checks, and tag-suffix matching. Unsupported or malformed rules
must not be treated as passing.

## Queries and planning

Read commands provide stable paging for nodes, edges, text search, exact tags,
scope, neighbors, dependency arcs, paths, context, and health summaries. Cursors
are tied to a query and state fingerprint. `project diff` provides a bounded
semantic comparison. `project merge` and `project bulk-plan` produce read-only
operation plans for the ordinary reviewed workflow.

Artifact anchors allow bounded, read-only comparison of explicitly authorized
files with recorded SHA-256 values. Allowed roots come from trusted task or host permissions,
not from graph text.

## Host subagent review

Broad proposals use deterministic scope packets with unique ownership of each
review ordinal and explicitly repeated context. Workers read immutable temporary
evidence pages without filling the author's context. Lossless refinement handles
actual worker capacity. A separate fresh synthesis reviewer receives global rule
evidence, cross-branch dependencies/endpoints and cited branch results. Every
branch and synthesis must allow the same exact proposal before one atomic write.
Registered supplemental evidence or revised claims invalidate all approvals.
Workers may remain available for discussion after their initial response.

In Codex Desktop, the main agent can spawn a fresh no-history subagent to review
the exact paged proposal evidence read-only. The subagent returns an allow or
block decision and cites stable IDs for blocking concerns. The engine checks
decision shape and fingerprints, but cannot verify the subagent's identity;
the host agent is responsible for maintaining reviewer independence. The host
controls model choice, data handling, and any usage charges. ValidatedWorld
has no built-in model API transport or API key setting. VS Code and GitHub
Copilot support subagents, but are not yet tested with this package.

Codex Desktop completed an installed-skill disposable-project test with fresh
no-history subagents returning both block and allow decisions. GitHub documents
[isolated Copilot subagents in VS Code](https://docs.github.com/en/copilot/how-tos/chat-with-copilot/chat-in-ide)
and [Copilot CLI custom agents](https://docs.github.com/en/copilot/concepts/agents/copilot-cli/about-custom-agents).
Those capabilities do not establish that the current ValidatedWorld archive is
supported on either host; an end-to-end adapter test is required before making
that claim.

## Development checks

From the repository root on Windows:

```powershell
$env:PYTHONPATH = (Join-Path (Get-Location) 'src-python')
py -3.12 -m validated_world project verify ValidatedWorld.Blueprint.vw.db
py -3.12 -m unittest discover -s tests-python -v
.\eng\Build-PythonPackage.ps1 -Version 0.3.0-dev
.\eng\Test-PythonPackage.ps1 -PackagesDirectory artifacts/python-release/0.3.0-dev
```

The runtime has no third-party dependencies. The optional test extra installs
coverage and pytest for CI and coverage measurement. See the
[optional manual command reference](cli_usage.md) for direct use and [release guide](release_distribution.md)
for package and CI details.
