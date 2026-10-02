# Python usage

ValidatedWorld is a standard-library Python package targeting Python 3.12 or
newer. Project knowledge is stored in one four-table SQLite `.vw.db` file.

## Development

From the repository root, install the optional test tools with `uv`:

```powershell
uv venv --python 3.12
uv pip install --python .venv/Scripts/python.exe -e ".[test]"
uv run --python .venv/Scripts/python.exe coverage run --branch -m unittest discover -s tests-python -v
uv run --python .venv/Scripts/python.exe coverage report
```

An ordinary Python installation may be used instead:

```powershell
py -3.12 -m venv .venv
.venv\Scripts\python.exe -m pip install -e ".[test]"
.venv\Scripts\python.exe -m coverage run --branch -m unittest discover -s tests-python -v
.venv\Scripts\python.exe -m coverage report
```

The runtime itself has no third-party dependencies. In a source checkout,
`$env:PYTHONPATH = (Join-Path (Get-Location) 'src-python')` makes
`python -m validated_world` available without installation.

## Public commands

```powershell
python -m validated_world project verify .\project.vw.db
python -m validated_world read ranked-search .\project.vw.db "retention policy"
python -m validated_world ndjson
```

The `ndjson` process keeps reviewed change sessions in memory only. A session
must present every exact-revision preview page before `change.write` or
`change.agent-write`; EOF,
cancel, or process loss discards an unfinished proposal.

Session snapshots are compact by default and omit full operation bodies and the
proposed graph. Use `change.affected` with a caller-selected `limit`, then follow
`page.nextCursor` until `page.isComplete` to collect exact affected-node,
edge-change, and scope-context evidence. `change.preview` is separately paged
and must be read completely before a write. For onboarding, verify the project,
confirm its purpose and status, then search and inspect bounded task-relevant
context. Add knowledge incrementally; a full-project import is not required.

## Host integration

The host agent authors through the NDJSON change commands. For a skill-led
write, it sends the exact paged proposal evidence to a fresh host subagent,
submits that decision through `change.agent-review`, and calls
`change.agent-write` only after an allow. A human can explicitly use
`change.write` after manual review. ValidatedWorld has no built-in model API
calls or API key configuration.

The SQLite provider does not load extensions. It verifies the schema, database
integrity, foreign keys, graph rules, and stored state fingerprint whenever it
opens a project.

The Python surface also provides read-only `project diff`, `project merge`,
`project bulk-plan`, `project export-sql`, built-in template commands, bounded
queries, artifact checks, and the complete in-memory `change.*` workflow. Use
`change.preview` until its `reviewPage.nextCursor` is null before
calling `change.write`; the cursor is bound to the exact proposal and page
size. `project merge` and `project bulk-plan` produce plans only and never
write a database.

The package is intentionally source-based. `Build-PythonPackage.ps1`
creates a standalone skill archive and a skills-only plugin archive, and
`Test-PythonPackage.ps1` extracts each archive and launches its Python entry
point outside the checkout. Project databases and credentials are not included.
