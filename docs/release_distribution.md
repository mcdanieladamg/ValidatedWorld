# Release and local plugin distribution

ValidatedWorld's primary agent-facing surface is the local plugin. The CLI
remains the durable manual, scripting, recovery, and source-checkout surface.
Both use the same Application layer and `.vw.db` format.

For the normal two-command development loop, start with the
[README](../README.md#build-and-install-the-local-plugin).

`Prepare-LocalPlugin.ps1 -NoRestore` reuses already restored solution and win-x64
CLI/MCP dependencies. It still builds and runs all offline checks; use it only
after those restores have succeeded.

## Build packages only (advanced)

For the normal build/test/install workflow, use `Prepare-LocalPlugin.ps1` and
then the install command it prints, as shown in the [README](../README.md#build-and-install-the-local-plugin).
Prepare already calls the build and release-test commands below. Use these
separately only when you need individual packaging or verification steps.

From a clean source checkout with the exact .NET SDK in `global.json`:

```powershell
dotnet restore ValidatedWorld.slnx
.\eng\Build-Release.ps1
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
.\eng\Test-Release.ps1 -Version <the-built-version> -RequireCodex
```

The smoke test uses paths with spaces and an isolated temporary Codex home. It
launches the packaged executables (not `dotnet`), creates and verifies a SQLite
project outside the installation, exercises the packaged MCP protocol and
`host_status`, rejects a deliberately mismatched manifest/binary version,
and confirms the external database survives. When available, it discovers the
Codex CLI from `PATH` or the local ChatGPT Desktop installation and also verifies
plugin installation, reinstallation, and removal. Pass `-CodexCommand` with a
specific `codex.exe` path when needed. Use `-RequireCodex` to make the complete
plugin lifecycle check mandatory for release acceptance. The script cleans up
only its unique temporary directory.

The update check runs `Install-LocalPlugin.ps1` against a previous marketplace
registration and compares the cached executable with the verified archive.
Checksums must cover both archives and the release notes; an empty or partial
checksum list fails. Checksums detect accidental changes, not publisher identity.

## Offline checks and manual CI packaging

PRs and pushes to `main` run only restore, build and the offline .NET tests.
Their pass/fail result appears in GitHub Checks; CI uploads no artifacts and
does not run packaging, installation or blueprint checks. Build is necessary
to compile the tests. The README badge links to that same status.

Run tests without live OpenAI calls or changing saved preferences:

```powershell
dotnet test ValidatedWorld.slnx --no-build --no-restore --filter "Category!=LiveOpenAI"
.\eng\Test-DeveloperTools.ps1
.\eng\Test-Blueprint.ps1
```

The unfiltered test command still honors the existing live-test opt-ins.
`Test-Blueprint.ps1` evaluates repository roadmap conventions through paginated
public CLI reads. It is repository tooling, not an implemented custom-rule
feature of the product.

The **Prepare Windows packages** Action runs only when manually dispatched. It
builds and tests archives and retains downloads for three days. It neither tags
nor publishes a release, installs into your desktop, nor supplies API credentials.
When Codex is absent, its host lifecycle check is explicitly skipped; run
`Test-Release.ps1 -RequireCodex` locally before accepting a release.

To use downloaded workflow artifacts, extract them to a folder and run:

```powershell
.\eng\Test-Release.ps1 -Version <version> -ArtifactsDirectory <folder> -RequireCodex
.\eng\Install-LocalPlugin.ps1 -Version <version> -ArtifactsDirectory <folder>
```

## Versions and reproducibility

Prepare, Build-Release, and the manual workflow share one version resolver:
`VersionPrefix` in `Directory.Build.props` plus `-dev.g` and the full Git `HEAD`
ID. It needs no tags, history depth, timestamp, machine name, or network lookup.
Automatic versions require a clean checkout; explicit `-Version` is available
for intentional releases and uncommitted local experiments. The `-dev.g` form
is reserved and must match the clean checkout even when supplied explicitly.
Commit IDs identify builds; they do not express chronological release ordering.

Packaging pins the SDK, uses deterministic compilation with normalized source
paths, normalizes package text to LF, and writes sorted ZIP entries with fixed
timestamps and no-compression mode. ZIPs are larger but avoid variable compression
settings. The same source, SDK, dependencies,
target and version are required for identical bytes; a shared version alone is
not proof. Build-Release always uses Windows PowerShell 5.1 for ZIP creation,
including when invoked from PowerShell 7, because their ZIP encodings differ.
Compare `SHA256SUMS.txt` from independent builds. Do not substitute
locally rebuilt files for tested release files without comparing their hashes.

Standard public GitHub runner time is free; retained artifact storage has an
account allowance. This optional workflow uses one standard Windows runner,
a 20-minute limit, three-day retention and read-only repository permission.
[GitHub billing](https://docs.github.com/en/billing/concepts/product-billing/github-actions).

## Other local agent hosts

The `codex plugin` installer is host-specific; the stdio MCP executable and
workflow skill are not. On Windows x64, build with `Build-Release.ps1` (no Codex
installation required), or download a tested archive. Extract the plugin ZIP
to a stable folder and configure your client's local **stdio** server command as
`<folder>/plugins/validated-world/bin/win-x64/ValidatedWorld.Mcp.exe` with no args.
Give the agent `<folder>/plugins/validated-world/skills/validated-world/SKILL.md`
as workflow instructions. These sources are also tracked under `packaging/`.

For VS Code/Copilot, use **MCP: Add Server** and select a local command. Keep
project-scoped configuration in source control with portable paths if sharing it;
keep credentials out. See [VS Code's MCP setup](https://code.visualstudio.com/docs/agent-customization/mcp-servers).
Then call `host_status`, select a disposable graph, and perform your own smoke
check. This is the generic integration route; Copilot acceptance is still pending
T29. When developing this repository, follow `AGENTS.md` and use the source CLI
for its blueprint regardless of host.

## Supported targets

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

Publishing and authentication remain manual. A GitHub release does not register
the plugin in a searchable catalog. Client packaging, catalog eligibility and
submission requirements are planned under `phase:t29` in the blueprint.

Official references:

- https://developers.openai.com/plugins/build/plugins
- https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview
- https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-publish
- https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases
