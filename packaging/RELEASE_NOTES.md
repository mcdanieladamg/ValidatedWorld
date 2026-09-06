# ValidatedWorld {{VERSION}}

Initial local-plugin distribution for Windows x64:

- Self-contained portable CLI; no .NET SDK/runtime required on the target.
- Local Codex marketplace with a workflow skill and bundled stdio MCP host.
- Bounded project discovery, reads, affected-context review, and guarded writes.
- Credential-free `host_status` diagnostics for version, runtime, installation,
  transport, and optional semantic-review configuration.
- User databases and settings remain outside replaceable application packages.

Tested support: Windows x64, Codex CLI 0.153.4 as bundled with the local ChatGPT
desktop host 26.901.41600. Other OS/architecture artifacts are intentionally not
claimed until they receive equivalent native SQLite and installation smoke QA.
