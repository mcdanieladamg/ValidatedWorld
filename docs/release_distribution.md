# Installation and packages

ValidatedWorld ships the same local Python engine in a standalone Agent Skill
archive and a skills-only plugin archive. Both require Python 3.12+ and an agent
host that can supply a fresh read-only subagent for reviewed updates. Codex
Desktop is the exercised host; other hosts require their own acceptance.

The engine has no third-party runtime dependencies and no model API key setting.
The host supplies agents, model selection and usage billing. Review evidence is
handled under that host's data policies. The workflow is English-only; stored
Unicode text round-trips without translation.

## Install

Install the standalone skill through your host's skill mechanism. For the Codex
plugin, use its marketplace installation mechanism. Public catalog distribution
is pending; the repository currently builds local candidate archives.

For a local Codex test of a built plugin candidate, run:

```powershell
.\eng\Install-LocalPythonPlugin.ps1 -Version <version>
```

This developer helper extracts the versioned archive under `artifacts/local-plugin`,
registers a local marketplace and installs `validated-world-python`. After
installation it removes older local Python plugin installations made by this
repository. Restart Codex and start a new task to use the installed skill.
Avoid selecting a separately installed older ValidatedWorld skill.

The package uses an available Python runtime, including an explicitly managed
`uv` environment when configured. It does not install a system runtime.

## Use and update

Ask the agent to maintain a project's connected knowledge, retrieve task context
or update decisions and their consequences. The
[skill instructions](../skills/validated-world/SKILL.md) describe the agent's
workflow. Browse the saved graph by opening its HTML file in a browser.

Keep project documents outside package/installation directories so replacing a
package preserves project knowledge. Updates replace the skill and shared
engine together. Unfinished proposals live in one process; finish or discard
them before restarting. A committed result awaiting publication has its own
reported recovery path.

Archives include source, instructions, metadata, license and the documentation
format. Project data, credentials, settings and compiled runtimes are excluded.
For build, package, CI and host acceptance methods, see
[developer verification](developer_testing.md). Optional direct commands are in
the [command reference](cli_usage.md).
