# ValidatedWorld {{VERSION}}

Initial local-plugin distribution for Windows x64:

- English-only supported product experience; Unicode graph text can be stored
  and round-tripped, but non-English workflows are not validated or supported.
- Self-contained portable CLI; no .NET SDK/runtime required on the target.
- Local Codex marketplace with a workflow skill and bundled stdio MCP host.
- Bounded project discovery, reads, affected-context review, and guarded writes.
- Credential-free `host_status` diagnostics for version, runtime, installation,
  transport, and optional semantic-review configuration.
- Interactive setup for an optional independent semantic reviewer using the
  user's own OpenAI API key.
- User databases and settings remain outside replaceable application packages.

Tested support: Windows x64, Codex CLI 0.153.4 as bundled with the local ChatGPT
desktop host 26.901.41600. Other OS/architecture artifacts are intentionally not
claimed until they receive equivalent native SQLite and installation smoke QA.
