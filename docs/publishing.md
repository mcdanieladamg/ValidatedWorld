# Publishing a release

This guide covers release builds, GitHub publication and public-directory
submission. GitHub availability and OpenAI directory approval are independent;
directory approval is not a prerequisite for publishing a GitHub release.
Version 1.0.0 establishes the document compatibility contract in
[document format](document_format.md#compatibility). Use the
[release notes](releases/1.0.2.md) as the GitHub release description;
[host acceptance](host_acceptance.md) records the scope and limits of the
external-machine reports.

## Local preparation

Use [developer verification](developer_testing.md) on the exact checkout to be
released. The package smoke also checks public listing text limits, portable and
Codex fallback metadata parity, bundled SVG branding, onboarding and policy
documents. These checks do not reproduce OpenAI's security scans or establish
catalog eligibility.

The source manifest is [portable plugin.json](../packaging/plugin/plugin.json).
OpenAI listing metadata lives in `extensions.com.openai.interface`; the Codex
compatibility manifest carries the same listing as a fallback. Root OpenAI
settings take precedence rather than merging with the fallback. Branding is
authored SVG under `packaging/plugin/assets`, covered by the project MIT
license. Icons have no external resources, scripts or font dependencies.

Build and verify the release, using an available Python 3.11+ interpreter.
Pick an unused version/output directory; builds do not
overwrite existing output.

```powershell
.\eng\Build-Package.ps1 -Version 1.0.2
.\eng\Test-Package.ps1 -PackagesDirectory artifacts/release/1.0.2 -PythonExecutable .\.venv\Scripts\python.exe
```

The ZIPs and `SHA256SUMS.txt` are regenerable outputs under
`artifacts/release/1.0.2`. The archives can be built and tested before the
human commits the release preparation. Before publication, merge exactly those
reviewed sources and target that commit with the release tag. If packaged sources
change after testing, build into a fresh output directory and test the new bytes.
Do not attach an archive whose sources differ from the tagged release.

## Clean-client acceptance

Install a candidate without relying on this checkout or an older installed
skill. Start a new chat and identify the bundled application version using
`python <skill>/scripts/validated_world.py --version`, and the Python interpreter
using `python --version`. Pip-installed package metadata is not required for
these source archives. Keep the test project outside installation directories.

Exercise a technical project and a non-code project: create and browse HTML,
retrieve bounded context, propose a change, obtain a fresh reviewer allow or
block, and save only an allowed exact proposal. Cover stale reviewer bindings,
batched branch review and synthesis, question workers, publication recovery,
package replacement and the strongly discouraged dependency-skip workflow.
Use the methods in developer verification and preserve sanitized reusable
findings rather than real project data. A host without persistent process access
or fresh read-only reviewers cannot provide ordinary reviewed writes.

Record actual client/OS/version results and distinguish maintainer reports from
independent measurements. Describe host capability requirements in public
documentation and use concrete compatibility reports to guide fixes. Inspect
enabled CI results separately. Do not mark public release work complete before
the remaining publication and directory conditions are met.

## Human publication

1. Publish the reviewed sources to the existing public repository. The manifest
   policy URLs target the `main` branch's privacy, terms and support documents;
   confirm those exact pages are publicly accessible after publication.
2. Draft a GitHub release targeting the tested commit and a matching tag, such
   as `v1.0.2`. Attach the generated plugin ZIP, standalone skill ZIP and checksum
   file. Use concise release notes describing requirements and tested clients.
   The automatic repository source ZIP is not the built installable skill.
3. Download and check the attached files before publishing the release. Keep the
   version, tag, source metadata and packaged metadata consistent.
4. In the OpenAI Plugins dashboard, select the owning organization/project,
   complete individual verification, and choose the verified developer identity.
   Upload the plugin ZIP and resolve validation/scan findings. Confirm listing
   text, icons, live policy links and advertised client behavior.
5. Submit for review and publish after approval. The skills-only package does
   not require MCP-specific review cases or a mandatory demo recording. Local
   tests and GitHub availability do not guarantee directory acceptance.
6. Add the resulting public listing and release links to the README. Complete
   the canonical project's ordinary reviewed release-state transaction only
   after the advertised acceptance and publication conditions are met.

Relevant official references:

- [OpenAI packaging](https://developers.openai.com/plugins/build/plugins)
- [OpenAI submission and metadata](https://developers.openai.com/plugins/deploy/submission)
- [OpenAI plugin guidelines](https://developers.openai.com/plugins/plugin-guidelines)
- [GitHub releases](https://docs.github.com/en/repositories/releasing-projects-on-github/managing-releases-in-a-repository)
