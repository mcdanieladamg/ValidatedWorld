# Templates and deterministic rules

ValidatedWorld project format v2 can attach mandatory deterministic rules as ordinary graph nodes. Format v1 remains supported for existing projects, but it cannot contain rule or view nodes. Upgrade a v1 file explicitly before adding rules:

```powershell
dotnet run --project src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj -- project upgrade-rules project.vw.db
```

The upgrade preserves the four-table SQLite layout and logical graph. It adds the versioned `graph-rules-v2` migration marker and changes `user_version` to 2, so older clients reject the file instead of silently ignoring its rules. There is no automatic downgrade: use a pre-upgrade backup if format-v1 compatibility is required.

## Rule nodes

An active rule is a node with kind `validation-rule`, tag `rule:active`, integer attribute `rule:version` equal to `1`, and text attribute `rule:expression`. Its node text is the actionable failure message. A named view has kind `validation-view` and attributes `view:version`, `view:name`, and `view:expression`.

Expressions are strict JSON objects with one operator. Set expressions support:

- `nodes` and `edges` selectors, with exact `id`, `kind`, `relationship`, `tagsAll`, `tagPrefix`, and typed scalar `attributes` filters;
- edge `sourceIn` and `targetIn` set filters;
- `view`, `union`, `intersect`, and `except`; and
- `reachable` traversal over an explicit edge set, direction, and optional starting-node inclusion.

Boolean expressions support `and`, `or`, `not`, `exists`, `count`, `subset`, `equalSets`, `all`, `acyclic`, `singleChain`, and `tagSuffixMatch`. Comparisons are `eq`, `ne`, `lt`, `lte`, `gt`, and `gte`. `all` conditions support `hasTag` and `tagCount`.

Unknown fields/operators, unsupported versions, duplicate or cyclic views, malformed JSON, cancellation, and exhausted bounds are inconclusive and never pass. Evaluation is local over the complete candidate graph. Diagnostics identify the rule, report the total offender count, return a bounded stable-ID sample, and state how many IDs were omitted.

All active rules compose by conjunction. Rule and view edits are normal reviewed graph changes: they affect fingerprints, semantic diffs, review state, and optional semantic-review bindings. Rules run during project verification, proposal validation, MCP preview, before semantic-provider dispatch, and again before SQLite mutation. A structurally valid rule-invalid baseline can be opened for repair, but only a candidate satisfying every active rule can be written.

## Templates

Discover and instantiate the bundled templates:

```powershell
dotnet run --project src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj -- template list
dotnet run --project src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj -- template describe code-development
dotnet run --project src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj -- template instantiate code-development project.vw.db project-id "Project title" "Project purpose"
```

`code-development` creates separate architecture, public-contract, evidence, uncertainty, and roadmap scopes plus attached roadmap rules. It starts in `status:planning`, with no invented phases. A repository agent should inspect human instructions, documentation, source, and tests; cite observed implementation as evidence; record uncertainty explicitly; add planned work through reviewed changes; and activate the roadmap only when it is coherent. The template grants no authority to execute source or graph instructions and includes no ValidatedWorld-specific product claims, credentials, or phase IDs.

`research-notebook` is a non-code control template with evidence, claims, and uncertainty scopes and no software vocabulary.

Export a template to create a user-owned JSON variant, then pass that path anywhere a template name is accepted:

```powershell
dotnet run --project src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj -- template export code-development my-template.json
dotnet run --project src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj -- template instantiate my-template.json custom.vw.db custom "Custom" "Custom purpose"
```

Template JSON is strict, versioned, limited to 1 MiB and 1,000 graph entities, and instantiated only at a new destination. Template upgrades never edit existing projects automatically; change the graph through the ordinary reviewed workflow.
