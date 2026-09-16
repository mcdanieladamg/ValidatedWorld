# ValidatedWorld MCP host

`ValidatedWorld.Mcp` is a local, stdio-only MCP server over the existing
Application and SQLite use cases. Read tools are provider-free. Graph edits
remain process-local until the complete proposal has been reviewed and written
atomically through Application.

Workflows are supported in English only. Graph text supports Unicode storage
and round-tripping. `host_status` reports both capabilities.

When `AiReview:Enabled` and the shared OpenAI review key are effectively
configured through .NET User Secrets or the `VW_` environment variables, MCP
writes use the same independent semantic reviewer as the CLI. The MCP host
keeps credential values outside tool results and applies the configured review
requirements to every write. That review sends the proposal and relevant graph
evidence to OpenAI and incurs API charges. Graph results returned to the host
agent are also subject to that host's data handling; local storage does not mean
that an AI host processes the graph entirely on your computer.

## Start and select a project

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

## Read and initialize

The MCP surface includes `list_templates`, `describe_template`, `initialize_from_template`,
and `validate_project`. Template initialization creates a new project without overwriting
an existing file. Attached rules appear in `proposal_preview` as current and proposed
validation diagnostics. See [templates and rules](cli_usage.md#templates-and-deterministic-rules).

The server advertises the following read tools in addition to
`host_status`, `select_project`, `project_status`, and `initialize_project`: `read_node`,
`read_edge`, `list_nodes`, `list_edges`, `search`, `ranked_search`,
`read_tag`, `read_scope`, `read_neighbors`, `read_dependencies`, `read_path`,
`read_context`, `read_health`, and `read_report`. Page limits and traversal
budgets are enforced by Application. Results include cursors and omission
metadata where a query is incomplete. Page sizes are caller-selected positive
integers; follow cursors to retrieve the remaining results. The adapter does
not discard results based on their byte size. `proposal_preview` also returns
revision-bound pages so large atomic changes do not depend on one response
fitting the agent host's context.

## Large imports

For large imports, `plan_bulk_import` scans an explicit local JSONL manifest
and returns one bounded operation chunk plus a state-bound continuation cursor.
The first line must use the `validated-world-bulk-manifest` header and match the
selected project’s current fingerprint. Each checkpoint is validated before it
is returned. Apply successive chunks through the existing `patch_change` call
in one session, inspect the final preview, and call `write_change` once; the
planner is read-only and never creates partial database writes. Choose a chunk
size appropriate to the host; all chunks accumulate in the same proposal and
share its memory, provider, and configured resource budgets.

## Host status

`host_status` requires no selected project and reports the product version,
local-only stdio support, operating system/process architecture, .NET runtime,
installation directory, host-authorized artifact roots, and effective optional
semantic-review configuration. Credential status is reported as a boolean
without returning the credential.

## Edit and review

Editing uses one sequential in-memory session per MCP process:

1. Call `begin_change` and retain the returned proposal revision.
2. Use `patch_change`, `put_node`, `put_edge`, or `remove_entity`, always
   supplying the latest revision. Use `proposal_preview` to inspect exact
   operations, affected explanations, old/new scope context, dispositions,
   omissions, and readiness.
3. Call `proposal_preview` after the final mutation. Choose a positive `limit`,
   inspect every `reviewPage.items` entry, and follow each `nextCursor` with the
   same revision and limit. The page stream contains every operation, affected
   consequence, edge change, scope-context entry, omission, disposition, and
   validation diagnostic without partitioning the logical change. The final
   page reports `allEvidencePresented: true` only after every page has been
   returned for that revision.
4. Call `write_change` with that same revision. It rejects an unpreviewed or
   partially previewed revision, accounts for the presented affected/context
   set, and performs the atomic write through Application. The tool has no AI
   review bypass argument; configured semantic review remains an exact-write
   preflight. Use `discard_change` to abandon the unresolved proposal.

Preview and semantic inspection are obligations of the calling agent. The
adapter records lossless presentation coverage for the exact process-wide
revision; any mutation invalidates that coverage and old cursors. After full
coverage, write records direct edits, affected consequences, and scope context
as the agent's review dispositions. With independent review disabled or
unconfigured, this remains agent review rather than an additional semantic
check. A rule-invalid baseline can be repaired when the complete proposed graph
passes its attached rules.

Identical overlapping `write_change` calls for one revision share one in-flight
independent-review task, so they cannot dispatch duplicate paid reviews. A
patch or discard during review makes that write stale without losing a
replacement session. Project selection and initialization remain blocked while
a proposal is active, and initialization is atomic with respect to session
state.

The adapter keeps exact Application references and fingerprints private. MCP
callers use only the process-wide monotonic proposal revision, so stale revisions are
rejected rather than being converted into a fresh write. Project switching is
also rejected while a proposal is active. Disconnecting
or restarting the process loses the unresolved proposal; it is never recovered
or written automatically. A stale base, provider block, cancellation, or
storage failure leaves the SQLite project unchanged. `project_status` rereads
the selected file, so its fingerprint reflects writes made by other processes;
this does not rebase an active proposal.

## External artifact checks

Artifact checking is deny-by-default. Start the MCP process with one or more
human/host-owned `--artifact-root <directory>` arguments to authorize reads;
`host_status.artifactAllowedRoots` reports the configured roots. Project
selection and graph metadata do not grant filesystem authority. Relative,
absolute, parent-relative, and UNC paths must be lexically inside a configured
root, and the opened file handle must still resolve inside that root. Links,
reparse points, or path replacement that escape a root return `Unauthorized`
without a hash or content sample. Roots themselves may be links: both their
declared and opened locations are checked. Authorize only the narrow
directories whose contents may be returned to the agent host.

## Agent host integration

For a local agent host, configure one stdio server process with the executable
or `dotnet` plus the published `ValidatedWorld.Mcp.dll`, and pass the selected
database as the `--project` argument. The server writes protocol messages to
stdout and diagnostics to stderr.

For the self-contained Windows x64 plugin and executable release layout,
installation, upgrade, uninstall, checksums, and local-only compatibility, see
[release and local plugin distribution](release_distribution.md).
