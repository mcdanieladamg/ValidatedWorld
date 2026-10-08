# ValidatedWorld

[![CI](https://github.com/mcdanieladamg/ValidatedWorld/actions/workflows/ci.yml/badge.svg)](https://github.com/mcdanieladamg/ValidatedWorld/actions/workflows/ci.yml)

ValidatedWorld helps AI agents maintain a graph of project knowledge and reason
consistently across projects larger than their context window. It tracks decisions
and dependencies, retrieves targeted context, and can fan out reviews to batches
of fresh, zero-context sub-agents so proposed changes receive focused, independent
scrutiny before they propagate through the project. The objective is graph-wide
conceptual consistency: not an absolute guarantee, but a systematic attempt to
detect and resolve inconsistencies throughout the connected knowledge base,
managed by your AI agent.

The graph is stored in your project as a human-friendly HTML file that you can open in a browser
to read and explore.

The agent keeps drafts and controller state in memory. Fresh sub-agents read
complete, bounded review packets from one hidden companion HTML file beside the
project document. Saving atomically updates the HTML file in your project; explicitly requested
backups and conversions remain supported.
Agent authoring sessions keep file access within the document's folder and its
subfolders.

ValidatedWorld is available as the **ValidatedWorld** plugin and as a standalone
**AI agent skill**. Ask the agent to understand the project or
make a change; it retrieves relevant context, reviews the affected knowledge and
saves the update in that HTML file.

Dependency links make review possible; missing links can hide consequences.
Review helps maintain consistency but does not prove that claims are true.

See [installation and packages](docs/release_distribution.md) for downloads and
setup.

After installation, start a new agent chat in your project and ask:
"Create a ValidatedWorld design document for this project." Then try:
"Find the decisions and dependencies relevant to my proposed change" or
"Update this design decision and review its consequences before saving."

- [Example: this project's browsable documentation](docs-vw.html)

- [Installation and packages](docs/release_distribution.md)
- [Agent skill instructions](skills/validated-world/SKILL.md)
- [Optional command reference](docs/cli_usage.md)
- [Documentation format](docs/document_format.md)
- [This project's browsable graph](docs-vw.html)
- [Developer verification](docs/developer_testing.md)
- [Privacy](docs/privacy.md), [software terms](docs/terms.md) and [support](docs/support.md)
- [Release preparation and publishing](docs/publishing.md)

[License](LICENSE)
