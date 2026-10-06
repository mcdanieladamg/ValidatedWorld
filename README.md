# ValidatedWorld

[![CI](https://github.com/mcdanieladamg/ValidatedWorld/actions/workflows/ci.yml/badge.svg)](https://github.com/mcdanieladamg/ValidatedWorld/actions/workflows/ci.yml)

ValidatedWorld is a graph of project knowledge: facts, decisions, evidence and
the relationships between them. It serves as project documentation and gives
an AI agent the context and consequences it needs to keep a project consistent.
When a decision changes, the agent follows its connections to review other
affected parts of the project.

Use it through an **AI agent skill**. Ask the agent to understand the project or
make a change; it retrieves relevant context, reviews the affected knowledge and
saves the update in `docs-vw.html` or a custom project file. You can also browse
the same graph by opening that HTML file in a browser with JavaScript enabled.

Dependency links make review possible; missing links can hide consequences.
Review helps maintain consistency but does not prove that claims are true.

See [installation and packages](docs/release_distribution.md) for downloads and
setup. Use the skill with an agent that supplies Python 3.11+, project-file
access, persistent processes and fresh read-only subagents for reviewed updates.

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
