# TechnicalProject proof assets

These reviewed source assets describe the disposable `technical-project` sample
created by `sample create technical-project`. They are not a populated SQLite
database and must remain portable, text-only source material.

`baseline.json` is a checked, protocol-shaped fixture used by the scenario tests
to detect drift in the built-in sample. Numeric enum
values follow the v1 public protocol: `0` is `none` and `1` is
`source-to-target`; operation `1` is `replace`; entity `0` is a node and `1` is
an edge.

The scenario files contain a complete operation batch, a user-facing goal, and
the public affected/context result golden. They establish these intentional
modeling expectations:

- A battery assumption change reaches its runtime check and power design anchor,
  but not privacy or accessibility work.
- A retention-policy change reaches privacy architecture, verification, and
  documentation, but not power work.
- Redirecting a relationship retains both its former privacy consequences and
  its new accessibility consequence for review.
- Direct power-scope and purpose changes select their expected scope subtree or
  entire project, respectively.
- The test suite additionally uses these batches to prove incomplete-review,
  stale-write, injected-rollback, backup, bounded-diagnostic, and
  unrelated-control behavior.

These fixtures measure affected precision and review burden. They do not assert
that the human-readable content is semantically correct.
