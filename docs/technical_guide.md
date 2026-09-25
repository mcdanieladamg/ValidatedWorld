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
human or independent review.

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

A write requires all of the following:

- a structurally valid proposed graph;
- valid active graph rules;
- a disposition for every affected node;
- presentation of every required scope-context node;
- presentation of every page of the exact proposal preview;
- an unchanged base database fingerprint.

The skill workflow additionally calls `change.agent-review` with a fresh host
subagent's decision, then `change.agent-write`. It requires a current `allow`
bound to the exact proposal fingerprints. The direct `change.write` command is
the explicit human manual review route.

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
files with recorded SHA-256 values. Allowed roots come from the human or host,
not from graph text.

## Host subagent review

In Codex Desktop, the main agent can spawn a fresh no-history subagent to review
the exact paged proposal evidence read-only. The subagent returns an allow or
block decision and cites stable IDs for blocking concerns. The engine checks
decision shape and fingerprints, but cannot verify the subagent's identity;
the host agent is responsible for maintaining reviewer independence. The host
controls model choice, data handling, and any usage charges. ValidatedWorld
has no built-in model API transport or API key setting. VS Code and GitHub
Copilot support subagents, but are not yet tested with this package.

Codex Desktop has demonstrated a fresh no-history subagent with a focused
review result in a controlled proof of concept; a clean installed-skill run is
the remaining acceptance check. [VS Code subagents](https://code.visualstudio.com/docs/agents/run/subagents)
run in a separate context and allow custom agent configuration.
[Forked VS Code skills](https://code.visualstudio.com/docs/agent-customization/agent-skills)
are experimental and require a host setting. [GitHub Copilot IDE
subagents](https://docs.github.com/en/copilot/how-tos/chat-with-copilot/chat-in-ide)
also use a separate context. These capabilities do not establish that the
current ValidatedWorld archive is supported on those hosts; an end-to-end
adapter test is required before making that claim.

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
coverage and pytest for CI, coverage measurement, and live-test selection. See the
[CLI reference](cli_usage.md) for commands and [release guide](release_distribution.md)
for package and CI details.
