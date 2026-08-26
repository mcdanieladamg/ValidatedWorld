# ValidatedWorld manual MVP release evidence

**Evidence date:** 2026-08-26

**Decision:** Continue, but describe the current result as a Windows x64,
framework-dependent developer preview of the manual engine. It supports the
deterministic part of the README's promise well enough to justify a separate
human decision about optional AI work. It is not evidence that arbitrary graph
text is semantically correct, that authoring relationships is easy, or that the
application is ready for a general end-user release.

## What the evidence supports

- The public CLI can create, inspect, query, review, atomically write, verify,
  back up, and export one current SQLite graph without SQL, a server, an ORM, or
  a provider call.
- The five TechnicalProject scenario goldens had no expected-node omissions or
  unrelated false positives. Review burden ranged from three affected nodes
  plus two context nodes for a local power change to all 13 nodes for a purpose
  change.
- Removed and redirected links retain their old consequences, scope context
  reaches the purpose without sibling fan-out, incomplete review blocks a
  write, and stale or injected failures preserve the prior fingerprint.
- At stress scale, inspecting only the changed seed's explicit outgoing arcs
  exposed 100 immediate links. The affected analyzer recursively selected all
  99,999 relevant non-purpose nodes, retained shortest paths of at most two
  edges, and added only the purpose as context. This is useful work beyond one
  ordinary local link lookup, while remaining entirely dependent on the links
  the author chose to model.

This supports the manual deterministic substrate of the central promise. It
does not replace semantic human judgment and cannot identify a relationship
that was never authored.

## Evidence environment and publish

The measurements ran on a Dell Precision 7750 with an Intel Core i7-10750H
(6 cores, 12 logical processors) and 68,334,764,032 bytes of physical memory.
The environment was Windows x64 build 10.0.26200, .NET SDK 10.0.400, and .NET
runtime 10.0.11.

The clean framework-dependent publish command was:

```powershell
dotnet publish src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj `
    -c Release --no-restore -o <disposable-output>
```

The publish completed without a warning and produced 41 files totaling
34,997,121 bytes. It included the managed SQLite packages and 22 native assets
for runtime families supplied by `SQLitePCLRaw.bundle_e_sqlite3`. Running the
published Windows executable from public help through sample creation, status,
and all nine verification checks used bundled SQLite 3.53.3 successfully. The
production dependency assets contained `Microsoft.Data.Sqlite.Core` 10.0.10
and `SQLitePCLRaw` 2.1.12; no ORM or OpenAI/provider package was present.

Only Windows x64 was runnable in this evidence environment. The portable
publish contained Linux and macOS native SQLite assets, but package presence is
not a runtime test. Linux, macOS, Arm, self-contained, installer, signing, and
versioned-distribution claims remain unverified and are not part of this release
recommendation.

## Scale measurements

Each row is one cold Release process and includes construction, deterministic
validation, state fingerprinting, one replacement projection, complete affected
analysis, and—where shown—SQLite initialization/load/verification. Times are
elapsed milliseconds from a single run, not benchmark guarantees. The graphs
used a shallow purpose scope and a two-hop semantic fan-out so path evidence
remained bounded while every node became relevant.

| Measure | Representative | Expected | Stress |
|---|---:|---:|---:|
| Nodes | 1,000 | 10,000 | 100,000 |
| Stored edges | 5,999 | 59,999 | 599,999 |
| Expanded review arcs | 10,000 | 100,000 | 1,000,000 |
| Graph construction | 16 ms | 109 ms | 996 ms |
| Structural validation | 76 ms | 375 ms | 3,102 ms |
| State fingerprint | 95 ms | 65 ms | 489 ms |
| Projection plus validation | 53 ms | 285 ms | 2,877 ms |
| Complete affected analysis | 67 ms | 471 ms | 4,156 ms |
| SQLite initialization | 826 ms | 4,589 ms | 45,366 ms |
| SQLite verified load | 157 ms | 1,112 ms | 19,193 ms |
| SQLite full verification | 163 ms | 902 ms | 18,732 ms |
| Database size | 905,216 B | 8,749,056 B | 89,276,416 B |
| Process peak working set | 84,062,208 B | 371,814,400 B | 2,683,940,864 B |

All three graphs validated, completed affected analysis without omissions, and
round-tripped with the same state fingerprint. They selected 999, 9,999, and
99,999 affected nodes respectively, plus only the purpose as context.

The maximum case is feasible on the recorded workstation, but a roughly
19-second verified load and 2.50 GiB peak working set are stress evidence, not
an interactive-service target. Deep scope chains, long explanation paths,
different metadata sizes, storage hardware, and other graph topologies can
change both time and memory substantially.

## Configured bounds reviewed

The public and persistence boundaries agree with the intended finite local
application:

- IDs are limited to 256 characters; ordinary text to 16,384; relationship
  labels to 1,024; and metadata names and canonical decimals to 256.
- Query pages default to 100 items and allow at most 1,000. CLI traversal
  defaults are depth 10,000 and 100,000 visited nodes.
- Structural validation defaults are depth 100,000, 1,000,000 node visits, and
  10,000 diagnostics. Affected analysis defaults are depth 100,000, 1,000,000
  affected nodes, and 1,000,000 output items. Reaching a traversal or output
  bound is explicit and inconclusive rather than silently complete.
- SQLite permits at most 100,000 nodes, 1,000,000 stored edges, 1 MiB per
  metadata JSON column, and a 16 GiB database file. Commands use a 10-second
  SQLite command timeout. The stress graph reached both the node limit and the
  design's one-million-review-arc target while remaining below the stored-edge
  limit.

## Published-artifact smoke result

Starting from `--help`, the published executable created a disposable
TechnicalProject database in a path containing spaces. A deliberately mistyped
node ID returned a precise not-found error. A page limited to two power-scope
descendants returned a cursor and one-item omission. A second mistaken
documentation ID was recovered through public search, after which the
retention-to-documentation path returned its expected two semantic edges.

The long-lived host then revised `battery-assumption`. Reusing the pre-apply
reference was rejected as stale. The exact proposal selected
`battery-assumption`, `power-design-anchor`, and `runtime-test`; context was only
`purpose` and `scope-power`; `retention-policy` remained excluded. Complete
manual dispositions enabled the write, the host exited with no warning or
stderr output, the new text persisted, and a verified online backup had the
same fingerprint.

The final completion sequence succeeded on 2026-08-26:

```powershell
dotnet restore ValidatedWorld.slnx
dotnet build ValidatedWorld.slnx --no-restore
dotnet test ValidatedWorld.slnx --no-build --no-restore
```

Restore used the repository's documented sandbox-only NuGet configuration
permission workaround. Build completed with zero warnings and zero errors. All
62 tests passed: 5 Core, 19 Validation, 4 Serialization, 21 Application, 8
SQLite persistence, and 5 CLI tests.

## Unresolved defects, concerns, and recommendation

No known deterministic correctness defect remained after the regression and
published-artifact smoke runs. The remaining concerns are release and product
evidence gaps:

- Manual authoring requires verbose NDJSON operations, exact fingerprint
  references, stable IDs, and deliberate relationship directions. This is a
  capable script surface, but a high-burden ordinary human interface.
- The realistic proof contains only 13 nodes, while the scale graphs are
  synthetic. Diverse long-lived projects have not established real-world link
  density, false-positive rates, omission rates, or modeling maintenance cost.
- The application guarantees modeled consequences, not semantic truth. A
  missing or incorrectly directed edge remains a human modeling error.
- There is no versioned distributable, installer, signing, cross-platform CI,
  upgrade history beyond SQLite v1, or recovery evidence for hostile operating-
  system failures. The README is accurate at the product-contract level, but it
  is not an installation or operator guide.
- Maximum-scale open and verification are seconds-to-tens-of-seconds operations
  with multi-gigabyte process memory on the evidence machine.

The explicit recommendation is **continue**, with the release claim narrowed to
an experimental Windows x64 developer preview of the manual engine. Do not
present T11 as evidence of semantic correctness or broad platform readiness.
Any move to the optional semantic reviewer or authoring agent remains a new,
human-authorized task with its own live-provider gate; T11 does not authorize
either feature.
