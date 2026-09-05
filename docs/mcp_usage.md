# ValidatedWorld MCP host

`ValidatedWorld.Mcp` is a local, stdio-only MCP server over the existing
Application and SQLite use cases. It does not add a second graph engine, does
not expose graph-edit tools, and does not make provider calls for reads.

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

The server advertises the following read-only tools in addition to
`select_project`, `project_status`, and `initialize_project`: `read_node`,
`read_edge`, `list_nodes`, `list_edges`, `search`, `ranked_search`,
`read_tag`, `read_scope`, `read_neighbors`, `read_dependencies`, `read_path`,
`read_context`, `read_health`, and `read_report`. Page limits and traversal
limits are enforced by Application. Results include cursors and omission
metadata where a query is incomplete; the host also applies a 512 KiB encoded
result bound.

For a local agent host, configure one stdio server process with the executable
or `dotnet` plus the published `ValidatedWorld.Mcp.dll`, and pass the selected
database as the `--project` argument. The server writes protocol messages to
stdout and diagnostics to stderr.
