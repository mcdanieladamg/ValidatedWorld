# ValidatedWorld MCP host

The MCP surface includes `list_templates`, `describe_template`, `initialize_from_template`,
and `validate_project`. Template initialization is explicit and non-overwriting; attached
rules then appear in every `proposal_preview` as current and proposed validation diagnostics.
The [CLI usage guide](cli_usage.md#templates-and-deterministic-rules) documents the shared
rule language and repository-bootstrap discipline.

`ValidatedWorld.Mcp` is a local, stdio-only MCP server over the existing
Application and SQLite use cases. Read tools are provider-free. Graph edits
remain process-local until the complete proposal has been reviewed and written
atomically through Application.

The initial public release is supported in English only. MCP tool descriptions,
messages, bundled workflow instructions, templates, examples, ranked-search
tuning, and optional AI authoring/review are authored and tested in English.
Graph text is Unicode and language-neutral storage and traversal can round-trip
other languages, but non-English workflow and retrieval quality are unsupported
and unvalidated. `host_status` reports both this product-language boundary and
the graph-text storage capability.

When `AiReview:Enabled` and the shared OpenAI review key are effectively
configured through .NET User Secrets or the `VW_` environment variables, MCP
writes use the same independent semantic reviewer as the CLI. The MCP host
keeps credential values outside tool results and applies the configured review
requirements to every write.

Build and run it from the repository root:

```powershell
dotnet run --project src/ValidatedWorld.Mcp/ValidatedWorld.Mcp.csproj -- `
    --project C:\data\world.vw.db
```

The optional `--project` (also `--default-project`) value is an explicit
default for that process. Without it, call `select_project` with an existing
`.vw.db` path or `initialize_project` to create a new purpose-only project.
Paths are interpreted on the executing host, normalized, checked for a real
`.vw.db` file, and kept outside the host installation directory. A selected
project is held by the stdio session; read tools do not accept arbitrary paths
and therefore cannot silently switch projects.

The server advertises the following read tools in addition to
`host_status`, `select_project`, `project_status`, and `initialize_project`: `read_node`,
`read_edge`, `list_nodes`, `list_edges`, `search`, `ranked_search`,
`read_tag`, `read_scope`, `read_neighbors`, `read_dependencies`, `read_path`,
`read_context`, `read_health`, and `read_report`. Page limits and traversal
limits are enforced by Application. Results include cursors and omission
metadata where a query is incomplete; the host also applies a 512 KiB encoded
result bound.

For large imports, `plan_bulk_import` scans an explicit local JSONL manifest
and returns one bounded operation chunk plus a state-bound continuation cursor.
The first line must use the `validated-world-bulk-manifest` header and match the
selected project’s current fingerprint. Each checkpoint is validated before it
is returned. Apply successive chunks through the existing `patch_change` call
in one session, inspect the final preview, and call `write_change` once; the
planner is read-only and never creates partial database writes.

`host_status` requires no selected project and reports the product version,
local-only stdio support, operating system/process architecture, .NET runtime,
installation directory, and effective optional semantic-review configuration.
Credential status is reported as a boolean without returning the credential.

Editing uses one sequential in-memory session per MCP process:

1. Call `begin_change` and retain the returned proposal revision.
2. Use `patch_change`, `put_node`, `put_edge`, or `remove_entity`, always
   supplying the latest revision. Use `proposal_preview` to inspect exact
   operations, affected explanations, old/new scope context, dispositions,
   omissions, and readiness.
3. Call `proposal_preview` after the final mutation and inspect the exact current
   operations and consequences. Its readiness still shows pending dispositions
   until the write is attempted.
4. Call `write_change` with that same revision. The adapter accounts for the
   presented affected/context set and performs the atomic write through
   Application. The tool has no AI-review bypass argument; configured enabled
   semantic review remains an exact-write preflight. Use `discard_change` to
   abandon the unresolved proposal.

The adapter keeps exact Application references and fingerprints private. MCP
callers use only the monotonic proposal revision, so stale revisions are
rejected rather than being converted into a fresh write. Project switching is
also rejected while a proposal is active. Disconnecting
or restarting the process loses the unresolved proposal; it is never recovered
or written automatically. A stale base, provider block, cancellation, or
storage failure leaves the SQLite project unchanged.

For a local agent host, configure one stdio server process with the executable
or `dotnet` plus the published `ValidatedWorld.Mcp.dll`, and pass the selected
database as the `--project` argument. The server writes protocol messages to
stdout and diagnostics to stderr.

For the self-contained Windows x64 plugin and executable release layout,
installation, upgrade, uninstall, checksums, and local-only compatibility, see
[release and local plugin distribution](release_distribution.md).
