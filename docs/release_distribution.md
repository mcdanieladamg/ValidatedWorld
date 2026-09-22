# Installation and release distribution

ValidatedWorld is distributed as source in two archives:

- a standalone Agent Skill; and
- a skills-only Codex plugin containing the same skill and Python package.

Both require Python 3.12 or newer, either from the host system or an explicitly
managed `uv` environment. Installation never creates or modifies a system
Python runtime.

## Build and verify archives

From the repository root in PowerShell:

```powershell
.\eng\Build-PythonPackage.ps1 -Version 0.3.0-dev
.\eng\Test-PythonPackage.ps1 `
    -PackagesDirectory artifacts/python-release/0.3.0-dev
```

The build creates versioned ZIP archives and `SHA256SUMS.txt` under the selected
output directory. The package test extracts each archive to a disposable
directory and launches its included engine outside the source checkout. Build
outputs are regenerable and are not durable project knowledge.

Release archives contain the skill instructions, Python sources, package
metadata, and license. They exclude project databases, credentials, local
settings, caches, and compiled platform runtimes.

## Development checkout

The runtime has no third-party dependencies:

```powershell
$env:PYTHONPATH = (Join-Path (Get-Location) 'src-python')
py -3.12 -m validated_world --version
py -3.12 -m unittest discover -s tests-python -v
```

For an isolated editable environment with the CI test dependency:

```powershell
py -3.12 -m venv .venv
.venv\Scripts\python.exe -m pip install -e ".[test]"
.venv\Scripts\python.exe -m coverage run --branch -m unittest discover -s tests-python -v
.venv\Scripts\python.exe -m coverage report
```

## Plugin installation

Install the generated plugin ZIP through the Codex app's normal local-plugin
workflow. Start a new agent task after installing or replacing the plugin so
the host loads the current skill instructions. The standalone skill archive is
for agent hosts that install skills without the Codex plugin manifest.

Keep user `.vw.db` files outside package or installation directories. Replacing
the package must not replace project databases or host settings.

## GitHub Actions

The CI workflow resolves three optional repository skip secrets and then runs
the Python suite on enabled operating systems:

```text
VW_CI_SKIP_WINDOWS
VW_CI_SKIP_LINUX
VW_CI_SKIP_MACOS
```

Absent, empty, or `false` runs that operating system. `true` deliberately skips
it. Invalid nonempty values fail configuration. A skipped job is reported as
excluded, never as a passed platform. Pull-request jobs do not receive OpenAI
credentials.

The standard unit and package workflow requires no repository secrets. Runtime
independent review uses the same environment setting names locally and in a
trusted, explicitly opted-in CI job:

```text
OPENAI_API_KEY
VW_AIREVIEW__OPENAI__APIKEY
VW_AIREVIEW__ENABLED
VW_AIREVIEW__PROVIDER
VW_AIREVIEW__MODEL
VW_AIREVIEW__TIMEOUTSECONDS
VW_AIREVIEW__MAXREQUESTBYTES
VW_AIREVIEW__MAXREQUESTITEMS
VW_AIREVIEW__MAXREQUESTTOKENS
VW_AIREVIEW__LIVETESTS
VW_AIAUTHORING__ENABLED
VW_AIAUTHORING__PROVIDER
VW_AIAUTHORING__MODEL
VW_AIAUTHORING__TIMEOUTSECONDS
VW_AIAUTHORING__MAXTOOLCALLSPERTURN
VW_AIAUTHORING__OPENAI__APIKEY
VW_AIAUTHORING__LIVETESTS
```

Store keys as repository or environment secrets, never in workflow files,
command arguments, packages, logs, or project databases. Set only the
feature-specific `__LIVETESTS` value(s) you intend to exercise to `true`; an
unset, empty, or `false` value makes no call for that feature. Live provider
checks run only from trusted code on `main` or an explicit trusted manual
dispatch, with sequential paid calls.

## Release review

Before publishing a release:

1. Verify every tracked `.vw.db` with the packaged Python command.
2. Run the complete unit suite and package extraction smoke on Windows.
3. Inspect the enabled Windows, Linux, and macOS GitHub Actions jobs.
4. Confirm any explicitly required live-provider check separately from offline
   success.
5. Inspect archive contents, hashes, versions, licenses, and actual sizes.
6. Install the exact candidate archive in a clean host and complete one
   disposable reviewed-write workflow.

Building, testing, or installing locally does not create tags, push commits, or
publish to a catalog.
