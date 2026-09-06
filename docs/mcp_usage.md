# ValidatedWorld MCP host

`ValidatedWorld.Mcp` is a local, stdio-only MCP server over the existing
Application and SQLite use cases. Read tools are provider-free. Graph edits
remain process-local until the complete proposal has been reviewed and written
atomically through Application.
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
3. Call `request_approval` for a complete, structurally valid proposal. The
   application writes the same complete preview and a one-time approval token
   to the MCP process diagnostic stream (`stderr`). The token is intentionally
   absent from the tool result, so an agent cannot manufacture human approval.
   After a human has inspected the local display, provide that token through
   `confirm_approval` with the displayed revision.
4. Call `write_change` with the revision returned by `confirm_approval`. This
   tool has no AI-review bypass argument; configured enabled semantic review is
   still an exact-write preflight. Use `discard_change` to abandon the
   unresolved proposal.

The adapter keeps exact Application references and fingerprints private. MCP
callers use only the monotonic proposal revision, so stale revisions are
rejected rather than being converted into a fresh write. Project switching is
also rejected while a proposal is active. Disconnecting or restarting the
process loses the unresolved proposal; it is never recovered or written
automatically. A human token, stale base, provider block, cancellation, or
storage failure leaves the SQLite project unchanged.

For a local agent host, configure one stdio server process with the executable
or `dotnet` plus the published `ValidatedWorld.Mcp.dll`, and pass the selected
database as the `--project` argument. The server writes protocol messages to
stdout and diagnostics to stderr.

For the self-contained Windows x64 plugin and executable release layout,
installation, upgrade, uninstall, checksums, and local-only compatibility, see
[release and local plugin distribution](release_distribution.md).
