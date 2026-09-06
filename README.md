# ValidatedWorld

ValidatedWorld is a local semantic change-control engine for large, connected
projects. It stores human-readable project knowledge as a graph, finds the parts
that a proposed change may invalidate, and requires those parts to be reviewed
before committing the new state.

This is a soft validation system, not a truth machine. Its value is a repeatable
procedure: keep important meaning and dependencies explicit, inspect a bounded
delta and its likely consequences, and preserve the reviewed result as the next
trusted baseline. The aim is to be more dependable than relying on one expert's
memory or giving a human or model an undifferentiated dump of the whole project.

The same model can represent software architecture, requirements, research,
patent outlines, novels, game lore, campaigns, or any other project whose facts
and decisions depend on one another.

## Why it exists

An AI can only read a fraction of a very large project at once. Ordinary search
can retrieve relevant text, but it cannot reliably identify every conclusion,
scene, requirement, or design decision that depends on a changed fact.

ValidatedWorld makes those relationships explicit. A local change produces a
bounded review set containing:

- the nodes and edges being changed;
- other nodes selected by the changed relationships;
- an explanation of why each item was selected; and
- the scope lineage from every selected node to the project's purpose.

The graph remains in one portable SQLite `.vw.db` file. The human or AI works
with focused slices while structural validation runs against the complete
project.

## How a change works

```text
describe or enter a change
        ↓
build an in-memory proposal
        ↓
validate structure and calculate affected nodes
        ↓
review the proposal, affected evidence, and scope context
        ↓
optional independent AI review
        ↓
atomically write the complete new graph, or write nothing
```

Semantic review is judgment, not mathematical proof. ValidatedWorld supplies
the relevant evidence and enforces the workflow; a human or AI decides whether
the affected text remains coherent.

## Project model

A project contains:

- **Nodes** — stable-ID units of human-readable meaning, such as facts, claims,
  requirements, events, decisions, tests, or artifact anchors.
- **Edges** — stable-ID labeled relationships between nodes. Each edge declares
  whether review propagates source-to-target, target-to-source, both ways, or
  neither way.
- **Scope tree** — exactly one purpose root and one `scope-parent` edge for every
  other node. This gives every node a unique path back to the project's purpose.
- **Tags and attributes** — searchable metadata that does not create hidden
  dependency behavior.

Every affected node contributes its full scope-upstream path as review context.
Those ancestors do not automatically pull in their other children, so a local
change stays local. Directly changing a scope node selects its descendants;
changing the purpose root is intentionally project-wide.

Pending operations and review state live in the running process. A successful
commit writes the complete reviewed graph in one SQLite transaction. Any stale
state, validation, constraint, I/O, or mapping failure leaves the previous graph
unchanged.

## Quick start

ValidatedWorld targets .NET 10.

```powershell
dotnet restore ValidatedWorld.slnx
dotnet build ValidatedWorld.slnx --no-restore
dotnet test ValidatedWorld.slnx --no-build --no-restore
dotnet run --project src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj -- --help
```

Create and explore the included technical-project sample:

```powershell
dotnet run --project src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj -- `
    sample create technical-project demo.vw.db
dotnet run --project src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj -- `
    shell demo.vw.db
```

Inside the shell, use `help`, navigate with `pwd`, `ls`, `cd`, and `root`, and
inspect the pending change with `changes`, `affected`, and `validate`. The shell
keeps the selected node, proposal, review coverage, and fingerprints in memory
until commit or discard.

Create a project with a purpose root:

```powershell
dotnet run --project src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj -- `
    project init project.vw.db my-project "My Project" purpose `
    "The purpose and governing constraints of this project."
```

Existing destination files are never overwritten by initialization, sample
creation, or backup commands.

## Finding project knowledge

Read commands return bounded results and support continuation cursors. Common
queries include:

```powershell
# Search node text and identifiers
dotnet run --project src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj -- `
    read search project.vw.db "authentication" --limit 20

# Find nodes and edges carrying an exact tag
dotnet run --project src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj -- `
    read tag project.vw.db status:current --limit 20

# Inspect scope, dependencies, neighbors, paths, or assembled context
dotnet run --project src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj -- `
    read dependencies project.vw.db requirement-login --max-depth 4 --max-nodes 100
```

Use `read --help` for the complete query surface.

## Comparing project versions

`project diff` produces a deterministic semantic comparison without modifying
either database or calling an AI provider:

```powershell
dotnet run --project src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj -- `
    project diff base.vw.db target.vw.db --limit 100
```

The result includes both fingerprints, project metadata changes, summary counts,
and stable-ID node and edge additions, replacements, and removals. Replacements
show old and new values plus the changed field names. Large results can be read
page by page with the returned cursor.

## Keeping the graph and project in sync

Projects can build deterministic publishers that read the reviewed graph and
generate final artifacts. Those project-specific tools remain responsible for
their output safety and correctness.

The normal repository workflow is paired change control:

1. Start from the last accepted `.vw.db` file and make a backup outside the
   repository.
2. Make one small, coherent graph change and the corresponding source,
   document, or content changes in the same branch and pull request.
3. Review `project diff` from the backup to the candidate database beside the
   ordinary source diff. Use bounded search, phase tags, dependencies, affected
   evidence, and scope context when the diff alone does not show enough meaning.
4. Accept and merge the database and external artifacts together. The resulting
   verified database becomes the trusted semantic baseline for later deltas.

Every meaningful pull request that changes tracked project artifacts is expected
to carry a `.vw.db` delta. If intended behavior, content, architecture, or design
changes, the delta updates that meaning. If a pull request implements work that
was already fully described, the delta records delivery by advancing the
relevant phase, status, or progress nodes, tags, and edges. The graph may lead
implementation only while those markers clearly distinguish planned, current,
and implemented work.

The narrow exception is corrective or non-semantic maintenance that changes
neither intended meaning nor any recorded delivery state—for example, repairing
an implementation so it finally conforms to an already-complete graph contract.
Such a pull request should say why the accepted graph already covers it instead
of manufacturing a meaningless database edit. Phase and status tags are a
project convention rather than hidden engine semantics, but they remain explicit,
queryable, and reviewable graph state.

Trusting the baseline means accepting its previously reviewed semantics without
re-litigating the entire project on every edit. `project verify` still checks
file identity, schema, integrity, and graph structure; it does not prove that the
stored claims are true. Pending proposals remain process-local, and an
unreviewed or uncommitted database edit is a candidate delta rather than a new
trusted baseline.

## Modeling guidance

- Use the scope tree for stable containment such as a subsystem, chapter, topic,
  or artifact boundary.
- Keep volatile names, counts, dates, complete lists, and conclusions in focused
  nodes rather than broad ancestors.
- Give stable IDs to enduring concepts instead of copying mutable display text
  into the ID.
- Direct semantic edges from a source of truth toward material that may become
  stale. Use bidirectional review only for genuine mutual dependence.
- Represent a closed collection with member-to-roster edges, then connect the
  roster claim to summaries or artifacts that repeat it. This avoids dependency
  cliques while still exposing stale counts and enumerations.
- Search for aliases and closed-world wording such as exact numbers, “all,”
  “only,” and “every” when adding or changing facts. A surprisingly small
  affected set often indicates a missing dependency.
- Use tags for organization and attributes for named scalar values. If one fact
  can invalidate another, represent that meaning with an edge.

Changing a `scope-parent` edge selects both the old and new child subtrees and
includes both ancestry lineages for review.

## Human and AI interfaces

ValidatedWorld provides a local agent plugin and three direct interfaces:

- local plugin — workflow guidance backed by the stdio MCP server;
- `shell <database>` — interactive manual authoring and review;
- `ai-assistant-shell <database>` — conversational authoring with bounded graph
  tools and explicit human approval; and
- `ndjson` — a structured protocol for agents, scripts, and integrations.

The AI author and semantic reviewer are separate roles. The author searches and
edits through the same guarded application operations available to a human. The
reviewer can allow or block the exact proposed write but cannot edit the graph.
Changing the proposal invalidates its approval and review decision.

The [CLI usage guide](docs/cli_usage.md) documents commands, response shapes,
pagination, graph traversal, manual review, and automation examples.

## Optional OpenAI configuration

All manual features work without an API key. AI authoring and review become
active when a key is configured and their respective switches are enabled.

| Setting | Default | Effect |
|---|---:|---|
| `AiAuthoring:Enabled` | `true` | Enables conversational graph authoring. |
| `AiReview:Enabled` | `true` | Requires independent semantic review before a normal write. |

For a source checkout, store the shared key in .NET User Secrets:

```powershell
dotnet user-secrets set "AiReview:OpenAI:ApiKey" "<key>" `
    --project src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj
```

`AiAuthoring:OpenAI:ApiKey` may be configured separately. Authoring otherwise
uses the review key and then `OPENAI_API_KEY`. Environment variables use the
`VW_` prefix and replace `:` with `__`, such as
`VW_AIREVIEW__ENABLED=false`.

Check effective configuration without displaying the key:

```powershell
'{"version":1,"command":"ai.status","payload":{}}' | `
    dotnet run --no-restore `
    --project src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj -- ndjson
```

Start conversational authoring with:

```powershell
dotnet run --project src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj -- `
    ai-assistant-shell project.vw.db
```

Before writing, the application shows the exact operations, affected evidence,
scope context, and fingerprints. The authoring agent cannot approve its own
proposal, bypass review, use raw SQL, or write directly. A configured reviewer
must return `allow`; blocks, malformed responses, timeouts, and provider errors
leave SQLite unchanged.

Manual operation remains available when AI features are disabled or
unconfigured. A human can explicitly bypass AI review for one otherwise valid
write with `commit --bypass-ai-review`.

## Boundaries

- Consistency depends on meaningful nodes and explicit dependency edges. The
  engine does not infer every unstated relationship from prose.
- The database represents current project knowledge, not commit history.
- ValidatedWorld identifies external artifacts that may be stale. Rendering and
  publishing remain the responsibility of project-specific tooling built on the
  public graph.
- Optional profiles may add domain vocabulary and deterministic checks without
  changing the plain node-and-edge model.

## Developing from the project blueprint

This repository uses
[ValidatedWorld.Blueprint.vw.db](ValidatedWorld.Blueprint.vw.db) as its detailed
product design, implementation contract, accepted semantic baseline, and ordered
roadmap. Planned work may appear before its implementation, but phase and status
metadata must make that distinction explicit. The repository therefore exercises
the same paired-change workflow intended for other large projects.

Begin a development run with bounded reads:

```powershell
dotnet run --no-restore --project src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj -- `
    project verify ValidatedWorld.Blueprint.vw.db
dotnet run --no-restore --project src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj -- `
    read tag ValidatedWorld.Blueprint.vw.db project:status --limit 10
dotnet run --no-restore --project src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj -- `
    read tag ValidatedWorld.Blueprint.vw.db status:current --limit 10
```

The status node identifies the current phase. Retrieve that phase's shared
`phase:<id>` tag, then inspect its context and dependency links before changing
code. The roadmap uses these conventions:

- one stable node tagged `project:status` and `current-phase:<id>`;
- phase nodes tagged `roadmap:phase`, `phase:<id>`, and exactly one of
  `status:pending`, `status:current`, or `status:complete`;
- an `estimate:small|medium|large|gigantic` tag on the current phase;
- matching phase tags on requirements, gaps, and recommendations; and
- explicit `precedes` and `scheduled-in` edges for ordering and traceability.

Keep stable project-lifecycle tags on the purpose root. Put frequently changing
status on its dedicated child because a direct root change selects the entire
project for review.

Implement only the current phase. Add focused automated tests, perform a
user-style smoke check through the public surface, and run the complete restore,
build, and test sequence. On success, update affected blueprint nodes through a
reviewed change session and atomically advance the old phase, new phase, status
node, and current-phase edge. On failure, leave phase state unchanged. Do not
edit the SQLite database directly.

Use `project backup` before a blueprint change and `project diff` afterward to
review its semantic changes alongside the source diff. Every successful phase
implementation therefore includes a blueprint delta even when its requirements
were already complete: the reviewed transaction records delivery and advances
the phase state. Only work that changes neither recorded meaning nor delivery
state may omit the database, and the review should explain that exception.

## Repository reference

- [CLI usage](docs/cli_usage.md) — end-user and integration reference
- [Project blueprint](ValidatedWorld.Blueprint.vw.db) — product design,
  implementation state, and roadmap
- [AGENTS.md](AGENTS.md) — repository-agent execution and safety rules

## License

See [LICENSE](LICENSE).
