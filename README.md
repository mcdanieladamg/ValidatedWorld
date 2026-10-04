# ValidatedWorld

[![CI](https://github.com/mcdanieladamg/ValidatedWorld/actions/workflows/ci.yml/badge.svg)](https://github.com/mcdanieladamg/ValidatedWorld/actions/workflows/ci.yml)

ValidatedWorld is a graph of project knowledge: facts, decisions, evidence and
the relationships between them. It serves as project documentation and gives
an AI agent the context and consequences it needs to keep a project consistent.
Changing a claim brings its downstream dependencies into review, along with
the upstream scope that gives those claims meaning.

Use it through an **AI agent skill**. Ask the agent to understand the project or
make a change; it retrieves relevant context, reviews the affected knowledge and
saves the update in `docs-vw.html` or a custom project file. You can also browse
the same graph by opening that HTML file in a browser with JavaScript enabled.

Dependency links make review possible; missing links can hide consequences.
Review helps maintain consistency but does not prove that claims are true.

The local packages require Python 3.12+ and a host with fresh read-only subagents
for reviewed updates. Codex Desktop is the exercised host. The current workflow
is English-only; Unicode graph text is preserved. Public distribution is pending.

- [Installation and packages](docs/release_distribution.md)
- [Agent skill instructions](skills/validated-world/SKILL.md)
- [Optional command reference](docs/cli_usage.md)
- [Documentation format](docs/document_format.md)
- [This project's browsable graph](docs-vw.html)
- [Developer verification](docs/developer_testing.md)

[License](LICENSE)
