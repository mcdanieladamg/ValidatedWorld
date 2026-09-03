# ValidatedWorld Product Roadmap

This is a transitional human-readable view of product priorities. The canonical
detailed roadmap is `ValidatedWorld.Blueprint.vw.db`, where implemented state,
gaps, recommendations, and their dependencies coexist. The ordered
implementation checklist remains [development_plan.md](development_plan.md)
until agents can depend on database-native planning tools.

## Required for repository adoption

1. **Track one canonical database.** Done for this repository: the root
   `ValidatedWorld.Blueprint.vw.db` uses ordinary Git, while SQLite sidecars and
   application-owned temporary files remain ignored. Do not use Git LFS by
   default.
2. **Keep a fingerprint-bound review projection.** The generated
   `samples/ValidatedWorldBlueprint/baseline.json` makes ordinary Git review
   possible without becoming a second source of truth. The repository script
   can regenerate or check it; wire that check into CI before supplementary
   Markdown is removed.
3. **Add semantic version diff.** Compare two databases by stable ID and report
   node/edge additions, replacements, removals, scope changes, review-direction
   changes, and old/new fingerprints. This is the highest-value missing feature
   for code-review use and should later support an optional Git diff driver.
4. **Add explicit graph merge after diff.** A three-way merge must reconcile
   compatible entity changes, reject conflicts, and run the result through
   affected analysis and review. Never merge SQLite pages directly.
5. **Standardize discovery.** README remains the zero-install bootstrap and
   points to the one top-level database. If a repository legitimately has more
   than one database, require an explicit manifest or command selection rather
   than guessing.

## Required before claiming the AI-first workflow is proven

1. **Safe bootstrap (T15).** Public project creation must produce only the
   purpose root. All useful graph content is then added piecewise through normal
   change, affected, manual-review, optional-AI-review, and atomic-write gates.
   Opening an existing verified database continues to trust its committed state.
2. **Bound the provider boundary (T16).** Measure the exact serialized review
   request locally before dispatch. Reject an oversized request without a paid
   call and explain which content caused it. Do not auto-partition it and do not
   review a whole project.
3. **Make omissions genuinely bounded (T16).** Report aggregate omission counts,
   a small sample, and paged detail. Never emit one metadata record per omitted
   node in the first response.

## Next usability improvement

4. **Ranked lexical discovery (T17).** Keep exact tag and literal search, then
   add deterministic ranking and match explanations. Teach the author to search
   aliases and closed-world language and to inspect dependency neighbors when
   an affected result looks suspiciously small.

This is the practical response to missing-edge risk: improve discovery and
authoring discipline. Similarity is not allowed to invent dependencies or turn
an open-world graph into a claim of completeness.

## Evidence-gated experiments

5. **Semantic retrieval (T18).** Compare semantic candidates with ranked lexical
   search on the ValidatedWorld blueprint and a different realistic corpus.
   Proceed only if the gain is material enough to justify privacy, provider,
   index, invalidation, and cost complexity.
6. **Query-path optimization.** A bounded query currently bounds returned
   content and AI context, not necessarily internal local work; loading and
   validating the complete SQLite graph is safe and does not send it to a
   provider. Optimize only after realistic measurements show unacceptable
   latency or memory use.

## Explicit non-goals

- whole-project AIReview, automatic reviewer partitioning, or parallel paid
  review calls;
- multiplayer or hosted collaboration;
- durable draft sessions;
- project history/undo in the canonical database;
- deterministic semantic contradiction proof;
- a maximum-scale benchmark program without a user-observed performance problem.

The product claim to prove is narrower and useful: an author can grow a project
through many small reviewed changes, recover relevant context later, see
declared consequences, and commit each approved change atomically without
requiring the project to fit in one model context.
