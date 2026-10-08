# Developer verification

This guide is for agents and humans developing ValidatedWorld. The installed
skill's users do not need to test generated documentation.

Roadmap checks cover planning, active and finished projects. A finished roadmap
has every phase complete, no current phase, and no current-phase tags or edges.
The developer-tooling regressions cover valid completed roadmaps and rejection
of unfinished phases or leftover pointers.

Use an already available Python 3.11+ executable. Check `--version`; if `python`
on PATH is older, pass the compliant executable explicitly. Do not install a
runtime as a test workaround. From the checkout root:

```powershell
$vwPython = 'C:\path\to\python.exe'
& $vwPython --version
$env:PYTHONPATH = (Join-Path (Get-Location) 'src')
& $vwPython -m unittest discover -s tests -p test_html_project.py -v
& $vwPython -m validated_world project verify docs-vw.html
& $vwPython -m coverage run --branch -m unittest discover -s tests -v
& $vwPython -m coverage report --skip-covered
.\eng\Test-DeveloperTools.ps1
.\eng\Test-Blueprint.ps1 -PythonExecutable $vwPython
.\eng\Build-Package.ps1 -Version 1.0.3
.\eng\Test-Package.ps1 -PackagesDirectory artifacts/release/1.0.3 -PythonExecutable $vwPython
.\eng\Test-PackageTempAlias.ps1 -PackagesDirectory artifacts/release/1.0.3 -PythonExecutable $vwPython
```

The build deliberately refuses an existing output directory. Inspect an old
release output before removing it, or select a fresh `-OutputDirectory` and pass
that exact directory to the package test. Archives are regenerable outputs.
The package test extracts each archive temporarily and exercises the included
launcher, document creation, import/export, verification and deterministic bytes.
Generated Python caches are excluded from all staged content, including helper
scripts and plugin resources; the extracted-package check rejects leaked caches.
It also copies only the complete skill folder away from the extracted package
and runs its launcher with Python's `-I -S` isolation, excluding `PYTHONPATH`,
user-site packages and site initialization. New-project initialization, sample
creation and round-trip checks use this source-only installation and project
files outside the skill folder, without pip-installed package metadata.
The isolated NDJSON smoke rejects a `host.help` request missing `payload`,
corrects it in the same process, then creates and verifies an HTML garden project.
It also executes the bundled command-reference templates through the isolated
launcher: create, retrieve, add a scoped claim, patch, page evidence, acknowledge
review, preview and publish. A write without host approval must block. Its later
allow is synthetic offline test data; this does not establish fresh-host review
acceptance, which requires the separate host smoke below.
The same walkthrough runs through the optional in-memory controller, receiving
each complete UTF-8 response before server exit. A child-process audit hook
rejects every filesystem write except the selected HTML, and rejects temporary
allocation, rename, hard links, directory creation and deletion. Both native
pipes and the controller must complete create/read/review/save without work
files. The controller is tested with loopback access; native pipes remain the
default when the host denies it. Unit tests cover read-only project directories,
launch-selected project roots for both transports, normalized inside paths,
outside read/write rejection, sibling-prefix escapes, links/junctions, Windows
device and stream aliases, and rejection of request-supplied root expansion.
Templates, backups and exports remain usable inside subfolders. Unit tests cover
RAM transaction rollback, optimistic stale checks, occupied destinations, direct
save failure and disposal of unsaved state. Packet tests fetch hashed bounded
pages in memory, require full presentation, reject stale evidence, revoke URLs,
and preserve Unicode graph IDs as data. Existing batching, refinement,
supplemental context, dialogue and synthesis gates continue to be exercised.
Windows disk-DB tests separately deny hard links for intentional initialization
and backup files. No test changes machine permissions.
CLI fixtures resolve OS temporary directories before selecting a persistent
project root and constructing file arguments. The review-packet sample generator
also resolves its caller's destination before starting a persistent session.
A regression exercises all four CLI fixture families and both game/scale
review-packet fixtures through a real directory alias and Windows short paths
when available. In-process controller tests cover authentication rejection,
malformed requests, bounded review packets, endpoint revocation and unsaved-work
cleanup; these complement child-process checks and count toward branch coverage.
The persistent-recipe regression executes the Python block from the agent
reference against the public launcher, retaining one session through an error,
Unicode authoring, review, guarded save and clean shutdown. Its approval is an
offline fixture; fresh-host review remains a separate check.
It also simulates a host denying strict path resolution while allowing the
selected working directory, so the recipe cannot reintroduce that preflight.
File-handoff regressions exercise complete affected analysis, independent
compressed-page reading, branch decisions and synthesis with socket creation
denied. They reject changed bytes, wrong bindings, incomplete receipts and stale
authoring references. Cleanup removes only owned unchanged files and preserves
unknown or modified entries. Compact-output tests retain exact references while
excluding evidence bodies and the save response's full decision collection.
Affected-page summaries read the nested pagination metadata and retain accurate
totals and continuation flags through the final page without exposing evidence.
These explicit reviewer handoffs are separate from the engine's no-implicit-file
audits above: the default skill route creates `.vw-review` under the selected
project and removes its owned artifacts after review or handled shutdown.
The temp-alias regression launches the same package smoke in a fresh shell with
an OS temp directory supplied through a directory link (a Windows junction or
Unix symlink). Both extracted and isolated trial paths use the physical directory;
the product continues to reject explicitly linked project paths. CI runs this
regression on each enabled platform.
The plugin check also enforces public listing text limits, portable/fallback
metadata parity, included onboarding and policy files, and safe square SVG
branding. It is local package validation, not a public-directory approval check.
Stable and development archive versions must match Python metadata, both plugin
manifests and the exact engine/launcher version output. To probe the stable
version guard, copy a release ZIP to a unique temporary directory, alter its
bundled version metadata, recompute that trial archive's checksum, and confirm
the package check rejects it before any project mutation. Keep the real release
archives unchanged.
See [publishing](publishing.md) for the remaining clean-client and human steps.
Offline commands make no product model API calls. Follow the repository's
bounded repair/retry rules; do not rerun unchanged failures hoping for success.
The coverage run executes the full unit suite and enforces the configured
threshold, so it need not be immediately duplicated. Without coverage, the
offline suite is `python -m unittest discover -s tests -v`; record that
coverage was not measured. For an intentionally provisioned developer environment:

```powershell
py -3.11 -m venv .venv
.venv\Scripts\python.exe -m pip install -e ".[test]"
```

The runtime has no third-party dependencies; the test extra provides coverage
and pytest. Use an existing environment for agent verification when available.

When using OpenAI's separate skill-creator `quick_validate.py` authoring check,
install its [PyYAML dependency](https://pyyaml.org/wiki/PyYAMLDocumentation) into
the developer environment. This helper is not bundled with ValidatedWorld;
running the product and building its archives do not require PyYAML.
Reuse the existing `.venv` rather than recreating it:

```powershell
.venv\Scripts\python.exe -m pip install PyYAML
$vwValidator = Join-Path $env:USERPROFILE '.codex\skills\.system\skill-creator\scripts\quick_validate.py'
.venv\Scripts\python.exe -X utf8 $vwValidator skills/validated-world
.venv\Scripts\python.exe -X utf8 $vwValidator packaging/plugin/skills/validated-world
```

The validator path above is the local Windows system-skill location; use the
path supplied by the active skill-creator instructions on another host.
UTF-8 mode makes the helper read Markdown consistently on Windows. PyYAML stays
in the developer environment, outside the product's runtime dependencies and
release archives.

CI runs verification, unit/coverage, blueprint and extracted-package checks on
Python 3.11 once per enabled Windows, Linux and macOS job.
`VW_CI_SKIP_WINDOWS`, `VW_CI_SKIP_LINUX`
and `VW_CI_SKIP_MACOS` control optional repository exclusions: absent/empty/false
runs a platform, true skips it, and other values fail configuration. Report
excluded platforms separately from passed checks; local Windows results do not
establish cross-platform acceptance.

## Disposable public workflow

Create a unique OS temporary directory and use the public checkout commands:

```powershell
$vwTrial = Join-Path ([IO.Path]::GetTempPath()) ('vw-smoke-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $vwTrial | Out-Null
$vwTrial = & $vwPython -c 'import pathlib, sys; print(pathlib.Path(sys.argv[1]).resolve())' $vwTrial
$vwDocs = Join-Path $vwTrial 'docs-vw.html'
& $vwPython -m validated_world sample create technical-project $vwDocs
& $vwPython -m validated_world project verify $vwDocs
& $vwPython -m validated_world read node $vwDocs purpose
& $vwPython -m validated_world read dependencies $vwDocs battery-assumption --limit 5
& $vwPython -m validated_world project backup $vwDocs (Join-Path $vwTrial 'before.html')
```

Use one persistent `ndjson $vwTrial` process for a reviewed change, following
[the manual command reference](cli_usage.md#persistent-ndjson-interface), with the
HTML file as `change.begin.path`. Inspect every affected/context and exact preview
page, then save. Compare bounded `project diff` with the backup, inspect changed
HTML source, reimport into a new temporary DB and verify it. Confirm that default
managed success leaves only the selected HTML and explicit developer baseline.
Exercise intentional DB-authority paths separately when changing those contracts.
Confirm read, init, review, discard and exit create no implicit files or folders.
Ordinary sessions have no filesystem lock; concurrent writers are unsupported,
and optimistic checks must reject an already changed base.

For project isolation, create a second project at an explicit custom path and
inspect both through the public CLI. A wrong project ID must reject change.begin.
Update the default file, verify both identities, and confirm every byte of
the other file is unchanged. Reuse an existing custom file rather than
creating a second copy under the default name.

For host acceptance use the exact candidate archive's launcher in a disposable
project. The main agent authors the proposal; a fresh read-only, no-history
subagent independently reads the bundled file handoff's complete exact evidence
and returns its binding, full receipt and unchanged cited decision. Use the
bundled `ReviewFiles.submit` helper for branch and fresh synthesis results, then exercise
`change.agent-write`. Include block and stale/incomplete evidence probes as
appropriate. Do not substitute same-context review or an installed old engine
for the candidate. This host check is separate from offline tests.

Explore realistic mistakes: corrupt a required JSON block and observe import
failure with no destination DB; restore it, edit a compatible record and confirm
import accepts it. A presentation-only edit must not alter graph meaning. Try an
occupied export destination and inspect the diagnostic. Unit fault injection
covers rendering failures before writing, changed source bytes, interrupted direct
saves and disposal of RAM state. Never
weaken OS permissions or bypass browser policy to complete a smoke check.

Remove owned temporary copies after inspecting their contents. Resolve each
recursive cleanup target and verify it remains under the unique trial directory.
Preserve unknown diagnostics or user files. Verify or restore any failed-save
HTML before reusing it; the product creates no recoverable WIP database.

## Browser smoke

Generate sanitized HTML files in the dedicated trial directory and serve only
that directory over loopback:

```powershell
& $vwPython -m http.server 8775 --bind 127.0.0.1 --directory $vwTrial
```

Keep the foreground command in an agent terminal session. Use the supported
Codex in-app browser at `http://127.0.0.1:8775/docs-vw.html`, for example
`cua.createBrowserTab("iab", url, {visible: false})`, and read its returned
documentation before further calls. OpenAI's
[browser guide](https://learn.chatgpt.com/docs/browser) describes local preview.
No full browser debugging permission or native-app access is needed. The
exercised agent browser rejects `file://` navigation; use its supported HTTP
preview without bypassing policy. Normal human double-click browsing needs no
server or network resources, but requires the inline JavaScript viewer. Report human file-opening and
agent preview checks separately. If supported browser or OS access is denied,
report the actual blocker and use the normal approval flow; do not change
security controls or attempt undocumented alternatives. Never serve the checkout
root or bind to all network interfaces.

Reload after re-exporting. Start at the real purpose node, follow a child,
breadcrumbs, a relationship, its endpoints and the tag index, using same-document anchors. Inspect a screenshot
for actual readability, not just a link assertion. Include long claims, unusual
text, typed attributes and a narrow viewport using the browser's documented
viewport capability; reset temporary overrides. Confirm the document has only
one passive JSON block, one hash-allowed inline viewer and local links. Check capitalization on headings, links
and prose after blank lines, including text that starts with a quote. IDs and
typed values must stay exact, and reimport must preserve original text casing.
Inspect console/CSP failures and deep-link reload after generated anchors exist.
Use hostile markup, unusual IDs, exact large integer strings and nullable values
to check safe DOM rendering and lossless data. Import must ignore a deliberately
broken viewer. Confirm a reviewed claim edit changes only its JSON line and update
timestamp, with identical shell/viewer bytes. Stop the preview server with Ctrl+C when
finished. Record actionable findings in the relevant regression or canonical
contract, not in dated transcripts or user-facing output.
