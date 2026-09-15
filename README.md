# ValidatedWorld

[![CI](https://github.com/mcdanieladamg/ValidatedWorld/actions/workflows/ci.yml/badge.svg)](https://github.com/mcdanieladamg/ValidatedWorld/actions/workflows/ci.yml)

ValidatedWorld is a local AI agent plugin that keeps project facts, decisions,
and their dependencies in one SQLite file. When an agent changes a fact, it can
see which connected claims need review, check the project's rules, and save the
reviewed change atomically. The current package uses the Codex plugin format.

Use it to document a project's entire knowledge base in an organized manner, to
prevent it from growing in a disorganized or undocumented manner. The eventual
goal is to organize every project from inception all the way up to the point that
it has accrued enough connected knowledge that keeping consistent is becoming
difficult even for high context-window AI agents to manage in one-shot prompts.
When adding content through this plugin, the agent must maintain the dependency edges:
missing links can hide stale claims, and a successful write does not prove the
text is true. Small projects may be better served by ordinary notes and tests.

The plugin is currently a Windows x64 prerelease with local installation;
public catalog distribution is still in preparation. The authoring agent uses
your chosen host. Optional independent review sends project evidence to OpenAI
using a separately configured API key and incurs API charges.

The initial public release is supported in English only. Graph text uses Unicode
and can store and round-trip other languages, but the product instructions,
diagnostics, bundled content, ranked-search tuning, and optional AI workflows are
authored and validated only in English; non-English workflow quality is not
currently supported.

## Build and install the local plugin

```powershell
# Build/test/package — repository root, clean commit, Windows x64, .NET 10 + Codex host
.\eng\Prepare-LocalPlugin.ps1

# Install/update — use the output from the command above, with the version printed, i.e.
.\eng\Install-LocalPlugin.ps1 -Version <printed-version>
```

## Use it

Restart the ChatGPT Desktop or Codex app after plugin install, start a new agent
chat, and instruct it to build or modify the db file with commands such as:

```text
Use ValidatedWorld to report host_status.

Use ValidatedWorld to create <absolute-path>.vw.db for <project and purpose>.

Use ValidatedWorld to open <absolute-path>.vw.db and summarize its purpose.

Use ValidatedWorld to make <requested change>; review affected knowledge and save it.
```

## Further reading

- [Technical guide](docs/technical_guide.md) — how the graph and review workflow work, CLI quick start, and OpenAI configuration.
- [Installation and releases](docs/release_distribution.md) — packaging, updates, and other local agent hosts.
- [CLI reference](docs/cli_usage.md) and [MCP reference](docs/mcp_usage.md) — commands and integration details.

## License

See [LICENSE](LICENSE).
