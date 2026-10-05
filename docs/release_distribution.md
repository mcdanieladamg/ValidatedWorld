# Installation and packages

ValidatedWorld ships the same local Python engine in a standalone Agent Skill
archive and a skills-only plugin archive. Both require Python 3.11+ and an agent
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

The standalone ZIP contains `SKILL.md` at its root. Extract it into a folder named
`validated-world` in the host's skill location (for current Codex local skills,
`~/.agents/skills/validated-world` or the project's `.agents/skills/validated-world`).
Install the entire folder, including `scripts`, `references`, `src-python` and
the license. The plugin contains that same complete skill under
`skills/validated-world`; it does not need application source outside that folder.
Host-specific installation and public-directory acceptance remain separate checks.

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
Python is the interpreter; the bundled `src-python/validated_world` files are
ValidatedWorld's application code. A host-provided interpreter can run them
without installing a global Python package or copying a developer's `.venv`.
Prefer runtime paths supplied by the agent host; otherwise check existing
interpreter commands and project environments. An older `python` on PATH does
not mean no suitable interpreter is available. Verify and use the selected
executable consistently; installation does not require changing PATH.
Check the application version through `python <skill>/scripts/validated_world.py
--version`. The bundled source does not require pip-installed distribution
metadata; `importlib.metadata.version("validated-world")` can raise
`PackageNotFoundError` even when the skill works correctly.

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

[Privacy](privacy.md), [software terms](terms.md) and [support](support.md) are
included with each installable skill and at the plugin root. The portable plugin
manifest includes listing text, starter prompts, policy URLs and light/dark SVG
icons; public policy URLs become available when the corresponding repository
files are published. [Publishing](publishing.md) describes release preparation
and the separate human publication steps.
