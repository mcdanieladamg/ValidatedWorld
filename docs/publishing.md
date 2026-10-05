# Publishing a release

The source and generated archives are local preparation. GitHub release
publication, verified publisher identity and public-directory approval are
separate human steps. Public distribution and the first supported compatibility
baseline remain pending.

## Local preparation

Use [developer verification](developer_testing.md) on the exact checkout to be
released. The package smoke also checks public listing text limits, portable and
Codex fallback metadata parity, bundled SVG branding, onboarding and policy
documents. These checks do not reproduce OpenAI's security scans or establish
catalog eligibility.

The source manifest is [portable plugin.json](../packaging/python-plugin/plugin.json).
OpenAI listing metadata lives in `extensions.com.openai.interface`; the Codex
compatibility manifest carries the same listing as a fallback. Root OpenAI
settings take precedence rather than merging with the fallback. Branding is
authored SVG under `packaging/python-plugin/assets`, covered by the project MIT
license. Icons have no external resources, scripts or font dependencies.

Build a fresh development candidate and verify it, using an available Python
3.11+ interpreter. Pick an unused version/output directory; builds do not
overwrite existing output.

```powershell
.\eng\Build-PythonPackage.ps1 -Version 0.3.0-dev.1
.\eng\Test-PythonPackage.ps1 -PackagesDirectory artifacts/python-release/0.3.0-dev.1 -PythonExecutable .\.venv\Scripts\python.exe
```

The ZIPs and `SHA256SUMS.txt` are regenerable outputs. Candidate versions are not
supported public releases. Before a final build, choose the release version and
compatibility promise, review the source changes, and have the human commit the
reviewed sources. Build the final archives from that exact commit with an
explicit version such as `0.3.0`, then test those same bytes.

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

Record actual client/OS/version results. Codex Desktop is the exercised host;
other clients and clean-machine installation need their own acceptance. Offline
Windows checks do not establish Linux, macOS or every ChatGPT surface. Inspect
enabled CI results separately. Do not establish a supported public compatibility
baseline or mark public release work complete before acceptance is resolved.

## Human publication

1. Publish the reviewed sources to the existing public repository. The manifest
   policy URLs target the `main` branch's privacy, terms and support documents;
   confirm those exact pages are publicly accessible after publication.
2. Draft a GitHub release targeting the tested commit and a matching tag, such
   as `v0.3.0`. Attach the generated plugin ZIP, standalone skill ZIP and checksum
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
