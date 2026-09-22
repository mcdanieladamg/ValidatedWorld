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
and any independent-review decision.

A write requires all of the following:

- a structurally valid proposed graph;
- valid active graph rules;
- a disposition for every affected node;
- presentation of every required scope-context node;
- presentation of every page of the exact proposal preview;
- an unchanged base database fingerprint; and
- when configured, an independent `allow` decision bound to the proposal.

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

## Optional independent review

The built-in reviewer uses the OpenAI Responses API only when it is both enabled
and configured through environment variables. It makes one request per new
proposal binding, performs no automatic paid retry, and treats transport,
timeout, refusal, malformed output, and `block` decisions as write blockers.
Manual review remains available without a key.

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
