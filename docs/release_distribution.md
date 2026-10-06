# Installation and packages

ValidatedWorld helps your agent keep a project's design and documentation
consistent as it grows beyond a single context window. Install it as a
standalone Agent Skill or a plugin containing the same complete skill.
Then ask your agent to create documentation, find relevant decisions and
dependencies, or review and save a change.

English is recommended for stored workflow guidance to help retrieval and review.
Project text can use other languages; Unicode is preserved without translation.

## Install

Download the standalone skill ZIP or plugin ZIP from the project's
[GitHub releases](https://github.com/mcdanieladamg/ValidatedWorld/releases).
Install the standalone skill through your host's skill mechanism. If installing
from a Codex marketplace listing, use that listing's installation mechanism.

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

Later 1.x releases preserve compatibility with valid 1.x project documents.
Incompatible document changes require a new major version and documented
conversion instructions. See [document compatibility](document_format.md#compatibility).

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
