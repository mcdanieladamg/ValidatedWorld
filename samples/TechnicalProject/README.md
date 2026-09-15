# TechnicalProject test fixtures

These fixtures describe the disposable `technical-project` sample
created by `sample create technical-project`. The JSON files are portable,
text-only inputs to deterministic scenario tests. The separate SQLite foundation
below is a starting graph for exploratory smoke testing.

The fixtures use English, the supported product language.

`baseline.json` is a checked, protocol-shaped fixture used by the scenario tests
to detect drift in the built-in sample. Numeric enum
values follow the v1 public protocol: `0` is `none` and `1` is
`source-to-target`; operation `1` is `replace`; entity `0` is a node and `1` is
an edge.

The scenario files contain a complete operation batch, a user-facing goal, and
the expected affected/context result. They cover:

- A battery assumption change reaches its runtime check and power design anchor,
  but not privacy or accessibility work.
- A retention-policy change reaches privacy architecture, verification, and
  documentation, but not power work.
- Redirecting a relationship retains both its former privacy consequences and
  its new accessibility consequence for review.
- Direct power-scope and purpose changes select their expected scope subtree or
  entire project, respectively.
- The test suite additionally uses these batches to check incomplete-review,
  stale-write, injected-rollback, backup, bounded-diagnostic, and
  unrelated-control behavior.

These fixtures measure affected precision and review burden. They do not assert
that the human-readable content is semantically correct.

## Exploratory smoke-test foundations

Create either starting point in a disposable folder:

From the repository root in PowerShell:

```powershell
$smokeDir = Join-Path ([IO.Path]::GetTempPath()) ('vw-smoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $smokeDir | Out-Null
# Broader technical-project graph: power, privacy, accessibility, documentation
dotnet run --no-restore --project src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj -- sample create technical-project (Join-Path $smokeDir 'technical.vw.db')
# Small synthetic privacy graph: purpose, policy, dependent check, unrelated control
dotnet run --no-restore --project src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj -- project backup samples/TechnicalProject/semantic-review-foundation.vw.db (Join-Path $smokeDir 'privacy.vw.db')
```

Open either copy in the CLI shell or select its path through any configured
local MCP client. `semantic-review-foundation.vw.db` contains only fictional
data (five nodes and five edges). Delete the disposable folder after testing.
Optional independent review sends evidence to OpenAI and incurs API charges
when enabled and configured.
