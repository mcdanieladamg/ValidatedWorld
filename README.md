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

ValidatedWorld is a Python 3.12+ source package delivered as a standalone Agent
Skill and a skills-only Codex plugin; public catalog distribution is still in
preparation. The authoring agent uses your chosen host. Optional independent
review sends project evidence to OpenAI using a separately configured API key
and incurs API charges.

The initial public release is supported in English only. Graph text uses Unicode
and can store and round-trip other languages, but the product instructions,
diagnostics, bundled content, ranked-search tuning, and optional AI workflows are
authored and validated only in English; non-English workflow quality is not
currently supported.

## Build the Python packages

```powershell
# Build source-based skill and skills-only plugin archives
.\eng\Build-PythonPackage.ps1 -Version 0.3.0-dev
.\eng\Test-PythonPackage.ps1

# Development checkout usage
$env:PYTHONPATH = (Join-Path (Get-Location) 'src-python')
python -m validated_world project verify .\project.vw.db
```

## Use it

Install the skills-only plugin archive through the host's normal local-plugin
workflow, ensure Python 3.12+ or an explicitly managed `uv` runtime is
available, then start a new agent chat. Direct local commands include:

```text
python -m validated_world project status <absolute-path>.vw.db

python -m validated_world project verify <absolute-path>.vw.db

python -m validated_world read ranked-search <absolute-path>.vw.db "authentication"

python -m validated_world ndjson
```

The `ndjson` process keeps reviewed proposals in memory, requires every exact
preview page before writing, and discards unfinished work on EOF or process
loss. See [Python usage](docs/python_usage.md).

## Further reading

- [Technical guide](docs/technical_guide.md) — how the graph and review workflow work, CLI quick start, and OpenAI configuration.
- [Installation and releases](docs/release_distribution.md) — packaging, updates, and other local agent hosts.
- [CLI reference](docs/cli_usage.md) — commands and the persistent NDJSON workflow.

## License

See [LICENSE](LICENSE).
