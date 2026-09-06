# ValidatedWorld local plugin installation

This archive is a local Codex marketplace containing the ValidatedWorld plugin
and its self-contained Windows x64 MCP host. It runs in local Codex hosts over
stdio.

After extracting the complete archive to a stable user-owned directory:

```powershell
codex plugin marketplace add "C:\path with spaces\validated-world-marketplace"
codex plugin add validated-world@validated-world-local
```

Start a new task so Codex loads the installed skill and MCP server. Ask it to
call `host_status`; verify product version `{{VERSION}}`, `local-only` support,
`stdio` transport, Windows x64 process architecture, and the expected semantic
review configuration. Database selection begins when you place a `.vw.db` path
in scope.

Upgrade by extracting the newer complete marketplace to a new stable directory,
checking that the manifest and `host_status` versions match, updating the local
marketplace source to that directory, and running:

```powershell
codex plugin marketplace upgrade validated-world-local
codex plugin add validated-world@validated-world-local
```

Use a new task after upgrade. To uninstall the plugin:

```powershell
codex plugin remove validated-world@validated-world-local
codex plugin marketplace remove validated-world-local
```

Keep `.vw.db` projects, backups, credentials, and project settings outside the
extracted marketplace and Codex plugin cache. Upgrade and uninstall replace or
remove plugin code only. Optional OpenAI semantic review uses the existing .NET
User Secrets identity or inherited `VW_AIREVIEW__*`/`OPENAI_API_KEY` variables.
