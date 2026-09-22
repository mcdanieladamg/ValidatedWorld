---
name: validated-world
description: Use the portable Python ValidatedWorld engine to inspect connected project knowledge, find consequences of proposed changes, and perform previewed atomic graph updates in a local .vw.db file.
---

# ValidatedWorld Python skill

Use the package's `validated-world` command or `python -m validated_world`.
When operating directly from the installed plugin, use
`scripts/validated_world.py`; it locates the bundled engine independently of
the current project directory.
Keep one `ndjson` process alive for a change session. Begin a change, apply or
patch operations, inspect affected evidence, review every affected node and
required scope context, then call `change.preview` and follow every
`reviewPage.nextCursor` until `allEvidencePresented` is true before writing.
The exact revision and fingerprints are application-owned and stale references
must be rejected.

The process keeps proposals in memory only. EOF, cancellation, or disconnect
discards unfinished work. Do not treat a successful atomic write as proof that
the graph claims are true. Search first, preserve meaningful dependency edges,
and use explicit artifact roots for read-only artifact checks.

Supported workflows are English-only. Unicode graph text is stored and
round-tripped. Optional independent OpenAI review is configured only through
environment variables and is never a reason to read or print a credential.
Optional built-in authoring is a separate paid Responses API conversation and
must still pass the exact preview, review, rule, and atomic-write gates.
