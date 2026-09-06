# Release and local plugin distribution

ValidatedWorld's primary agent-facing surface is the local plugin. The CLI
remains the durable manual, scripting, recovery, and source-checkout surface.
Both use the same Application layer and `.vw.db` format.

## Build locally

From a clean source checkout with the .NET 10 SDK selected by `global.json`:

```powershell
dotnet restore ValidatedWorld.slnx
.\eng\Build-Release.ps1 -Version 0.1.0
```

The build script performs runtime-specific restores, publishes self-contained
single-file Windows x64 executables, assembles the local marketplace, verifies
manifest/binary version agreement, creates archives with normalized entry
timestamps, and writes SHA-256 checksums.

Outputs are placed under `artifacts/release/<version>/`:

- `validated-world-cli-<version>-win-x64.zip`
- `validated-world-plugin-<version>-win-x64.zip`
- `RELEASE_NOTES-<version>.md`
- `SHA256SUMS.txt`

Run the install/upgrade/uninstall smoke test against those exact archives:

```powershell
.\eng\Test-Release.ps1 -Version 0.1.0
```

The smoke test uses paths with spaces and an isolated temporary Codex home. It
launches the packaged executables (not `dotnet`), creates and verifies a SQLite
project outside the installation, exercises the packaged MCP protocol and
`host_status`, rejects a deliberately mismatched manifest/binary version,
and confirms the external database survives. When available, it discovers the
Codex CLI from `PATH` or the local Codex desktop installation and also verifies
plugin installation, reinstallation, and removal. Pass `-CodexCommand` with a
specific `codex.exe` path when needed. Use `-RequireCodex` to make the complete
plugin lifecycle check mandatory for release acceptance. The script cleans up
only its unique temporary directory.

## Compatibility

The prepared matrix currently contains only Windows x64. Self-contained .NET
single-file outputs are runtime-specific, and the native SQLite provider must be
smoke-tested on every added target before that operating system and architecture
is listed as supported.

The plugin runs locally over stdio. Its relative launch configuration supports
installation under any stable user-owned directory. Project databases, settings,
and credentials remain outside the plugin installation directory.

## Publishing a GitHub release

GitHub Releases can host the four generated files. The current documented limit
is 1,000 assets per release and less than 2 GiB per asset; the prepared set is
well below both. Create a matching version tag and release, attach the two
archives, notes, and checksum file, and verify the uploaded checksums.

Official references:

- https://developers.openai.com/plugins/build/plugins
- https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview
- https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-publish
- https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases
