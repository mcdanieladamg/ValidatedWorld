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

## Optional independent semantic review

The plugin's author is the Codex agent in your task. Independent semantic
review is a separate, fresh OpenAI API request containing the bounded proposed
transaction and its review evidence, not the authoring conversation. It runs
before `write_change` only when review is enabled and an API key is configured.

To enable it with your own OpenAI API key, run this from the extracted plugin
directory:

```powershell
.\plugins\validated-world\scripts\Configure-Review.ps1
```

The script prompts without echoing the key and stores the settings as
user-level Windows environment variables. Restart Codex afterward, start a new
task, and ask the agent to call `host_status`; `semanticReview.effective` should
be `true`. User-level environment variables are readable by processes running
as your Windows account, so remove the key when it is no longer needed:

```powershell
.\plugins\validated-world\scripts\Configure-Review.ps1 -RemoveKey
```

To disable review while retaining the configured key:

```powershell
.\plugins\validated-world\scripts\Configure-Review.ps1 -Disable
```

Without an effective reviewer, the normal previewed and atomic MCP workflow
still works. Reviewer credentials are configured independently of the Codex
host, and the review request is sent directly to OpenAI.

To replace an installed copy, extract the newer complete marketplace archive to
a new stable directory. Then remove the cached plugin and old marketplace
source before registering and installing the new directory:

```powershell
codex plugin remove validated-world@validated-world-local
codex plugin marketplace remove validated-world-local
codex plugin marketplace add "C:\path with spaces\new-validated-world-marketplace"
codex plugin add validated-world@validated-world-local
```

Restart Codex and use a new task after replacement. Ask the agent to call
`host_status` and confirm the expected product version. To uninstall the plugin:

```powershell
codex plugin remove validated-world@validated-world-local
codex plugin marketplace remove validated-world-local
```

Keep `.vw.db` projects, backups, credentials, and project settings outside the
extracted marketplace and Codex plugin cache. Upgrade and uninstall replace or
remove plugin code only. Optional OpenAI semantic review uses the existing .NET
User Secrets identity or inherited `VW_AIREVIEW__*`/`OPENAI_API_KEY` variables.
