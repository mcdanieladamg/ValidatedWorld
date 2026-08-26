# Lore graph modeling study

**Study date:** 2026-08-26

**Status:** Design evidence, implemented scope-topology correction, and durable
modeling recommendations.

## Question and conclusion

The study used the requested Skyrim-like abstraction: a world called Tamriel,
five continent-like regions including Skyrim, detailed lore under Skyrim, and
a proposed sixth region. The labels are deliberately simplified for graph-
design testing and are not a claim about authentic Elder Scrolls geography.

The existing engine is useful, but only when authors distinguish containment
from semantic consequence. It does not infer that a child contributes to a
count, nor that text mentioning a name depends on the canonical name. A compact
directional roster model solves the sixth-continent problem without making all
continents co-dependent.

One material engine defect was found and corrected: changing only a
`scope-parent` edge could move an existing subtree while producing no affected
nodes or context and a review-ready proposal. Scope-topology operations now
select the moved child surface and both immediate parents without sibling
fan-out.

## What the current rules actually mean

The scope tree provides containment and mandatory upstream context. It is not a
computed ontology:

- Adding a node makes that node a direct change. Adding its `scope-parent` makes
  the immediate parent an affected membership obligation; higher ancestors are
  context, and siblings are not selected.
- Editing a scope node directly selects its current and proposed descendants.
- Merely showing an ancestor as context does not propagate through the
  ancestor's children or semantic edges.
- Ordinary edges propagate recursively according to their explicit review
  direction. Labels alone have no behavior.
- Changed ordinary edges use both their old and new review arcs. A changed
  `scope-parent` has direction `none`, but its special topology rule selects
  old/new child subtrees and immediate parents plus both ancestry lineages.
- The validator proves graph structure, not claims such as “exactly five”.

This means a new sixth continent cannot literally be invisible: the new node
must be reviewed and its scope ancestors must be presented. However, a separate
sibling claim saying “there are five” is invisible to that review unless an
edge connects the member to the claim. If the count is embedded in an ancestor,
the reviewer sees it as context but must notice and reconcile it semantically.

## Models exercised through the public CLI

All graphs were initialized through NDJSON `project.init`. Every proposal used
`change.begin`, `change.apply`, affected/readiness inspection, and
`change.discard`. One linked-roster proposal also completed review,
`change.write`, reopen verification, and public search. No private API or direct
SQL mutation was used.

### Sibling count claim

```text
purpose
└─ Tamriel
   ├─ geography
   │  ├─ Skyrim
   │  ├─ Cyrodiil
   │  ├─ Morrowind
   │  ├─ Hammerfell
   │  └─ High Rock
   └─ “Tamriel has exactly five continents”
```

Adding Atmora beneath geography selected Atmora and geography, with Tamriel and
purpose as context. The sibling count claim was absent. This reproduces the
semantic modeling problem in the question while still reviewing the changed
membership of the immediate parent.

### Counting parent

Putting the exact count and list in the continents' immediate parent made it
visible as context when Atmora was added. That is better for detection, but
correcting the parent's text made the parent a direct scope change and selected
all five existing continent nodes plus Skyrim's local descendants. It couples a
frequently changing aggregate to a deliberately broad scope operation.

### Directional linked roster

```text
purpose
├─ Tamriel
│  ├─ geography
│  │  ├─ stable continent scope ──member-of-roster──▶ roster claim
│  │  └─ local lore beneath each continent
│  └─ “exactly five: ...” roster claim
├─ documentation ── atlas summary ◀──informs── roster claim
└─ narrative ────── sailor dialogue ◀─informs── roster claim
```

Each continent identity points `sourceToTarget` to the roster claim. The roster
points `sourceToTarget` to the statements and artifact anchors that repeat its
count or list. Existing continents do not point to one another. A separate
canonical-name fact under Skyrim points to the roster and to the travel brochure
that repeats the name.

This direction matters. A member change reaches the roster and consumers. A
roster correction reaches its consumers but does not walk backward into every
member. A local lore fact has no roster edge and stays local.

## Observed affected sets

The small graphs contained five continent identities and three detailed Skyrim
facts. The expanded linked graph contained 74 nodes: five continent scopes,
twelve lore facts per continent, one roster, and three cross-scope consumers.

| Proposal | Affected | Context | Important observation |
|---|---:|---:|---|
| Add sixth; sibling count claim | 2 | 2 | Parent reviewed; count claim missed |
| Link five existing members to sibling count | 6 | 3 | No local lore selected |
| Migrate links and add sixth together | 8 | 2 | No local lore selected |
| Add sixth beneath counting parent | 2 | 2 | Counting parent is affected |
| Add sixth and edit counting parent | 10 | 2 | Entire parent subtree selected |
| Add sixth but omit roster edge | 2 | 2 | Roster and consumers missed |
| Add sixth with roster edge | 5 | 4 | Member, parent, roster, two consumers |
| Add sixth with five `both` co-dependencies | 10 | 4 | All continent identities selected |
| Add sixth and update linked roster | 5 | 4 | Existing members excluded |
| Change local Whiterun lore | 1 | 4 | Correctly local |
| Change canonical Skyrim name fact | 5 | 6 | Roster and named consumers selected |
| Reframe Skyrim scope | 8 | 5 | Local subtree plus true consumers |
| Delete leaf continent and incident edges | 5 | 4 | Old parent and consumers selected |
| Reparent Skyrim using only `scope-parent` | 10 | 3 | Subtree, parents, and consumers selected |
| Reparent plus unchanged node replacement | 10 | 3 | No workaround or smaller surface |
| Expanded: add sixth with roster edge | 5 | 4 | Independent of 60 existing lore facts |
| Expanded: update roster only | 3 | 4 | Members excluded by direction |
| Expanded: change one local fact | 1 | 4 | Correctly local |
| Expanded: change canonical name fact | 5 | 6 | Stable at larger local detail volume |
| Expanded: reframe one continent scope | 17 | 5 | 12 local facts plus true consumers |
| Expanded: change Tamriel scope | 71 | 3 | Intentionally nearly world-wide |
| Expanded: change purpose | 74 | 0 | Correctly project-wide |

The gradient is useful: one local fact stayed at one affected node; a reusable
name reached five; a new roster member reached five including its parent; a continent-wide premise
reached seventeen; Tamriel-wide meaning reached seventy-one; and a purpose
change reached every node.

Public search also helped discover candidate links. Searching `five continents`
found exactly the roster, atlas, and sailor dialogue. Searching `Skyrim` found
the scope, name fact, local lore, brochure, and related edges. Search results
still require semantic judgment; substring occurrence is not proof of
dependency.

## Complete write walkthrough

The committed disposable transaction added Atmora and its scope/membership
edges, changed the roster from five to six, and updated the atlas and sailor
dialogue. Its affected set was those four directly changed/semantic nodes plus
the geography parent, whose membership changed. Four scope-context nodes were
presented; the four direct nodes were marked updated, geography was marked
reviewed-no-change, readiness became true, and `change.write` succeeded.

The reopened project verified with 18 nodes and 27 edges. Searching
`six continents` returned the roster, atlas, and sailor dialogue. This proves
the model is operable through the current manual product, not merely plausible
on paper.

## Recommended default lore model

### Keep scope nodes stable and boring

A scope should answer “what body of material belongs here?” It should not be the
only home of a volatile exact count, current name, or enumerated roster. Direct
scope edits intentionally have broad meaning and therefore broad review.

For the example:

- `tamriel` is a stable world scope.
- `tamriel-geography` is a stable organizational scope.
- `continent-skyrim` is a stable identity/scope with a stable opaque ID.
- `skyrim-canonical-name` is a leaf claim that may change.
- `tamriel-continent-roster` is a leaf aggregate claim with the exact count and
  list.

### Use fan-in/fan-out hubs, not sibling cliques

Point each member toward one roster claim. Point that claim toward exact-count
and exact-list consumers. This produces a small semantic hourglass:

```text
members ──▶ roster/count claim ──▶ summaries, dialogue, tests, anchors
```

Do not connect all members with `both` merely because they share a category.
The test selected nine nodes under that strategy versus four with the roster,
and the extra five were identities that had no text to revise.

### Separate identity from mutable naming

Keep a stable ID such as `continent-skyrim` even if its display name changes.
Put the canonical name in its own claim and link that claim to chunks that repeat
the name. The test changed the name without selecting unrelated climate or city
lore. Editing the continent scope itself remained available for a true
continent-wide reframing and correctly selected the local subtree.

### Link at meaningful artifact granularity

Every occurrence of a word does not need an edge. Link the canonical claim to a
scene, quest, dialogue entry, document section, database record, or external
artifact anchor that a reviewer can actually revise. An authoring AI should
search for aliases and exact wording, propose candidate links, and ask when a
reference is ambiguous.

### Treat closed-world wording as special authoring work

Words such as exact numbers, “all”, “only”, “every”, and complete enumerations
assert closure. The common engine is deliberately open-world and will not infer
those constraints. An authoring AI should isolate such wording in a claim,
search for its members and consumers, and create explicit directional edges.

## Migrating an existing graph

The naive sibling-count graph converted cleanly by adding one directional edge
from each existing continent to the count claim. The one-time migration selected
the five continent identities and the count claim—six nodes—but none of Skyrim's
local lore. Migrating and adding the sixth in one transaction selected those
seven nodes plus the geography parent and still excluded local detail.

If the count is embedded in a scope parent, extracting it into a separate roster
claim requires editing the scope node. That intentionally selects its existing
subtree once. This is a legitimate review cost for changing the meaning of a
scope, not a reason to split the work into transactions that hide consequences.

An edge-only reparent now selects the moved subtree, its semantic consequences,
and the old/new parents. Replacing the subtree root with unchanged content is no
longer necessary and does not reduce the review surface.

## Engine findings and follow-up order

### 1. Scope-topology changes are corrected

For every added, removed, or replaced `scope-parent` edge, affected analysis
should:

1. Seed the old and new child endpoints.
2. Select the child's current and proposed descendant subtrees.
3. Make the old and new immediate parents explicit non-direct review
   obligations.
4. Include both old and new upstream lineages as context.
5. Preserve the no-sibling-fan-out rule unless a sibling is reached by an
   explicit semantic edge.

Old/new parents would be affected because their membership changed, not merely
because they are ancestors. They would not be direct node changes, so they would
not trigger scope-descendant expansion. Their explicit semantic outgoing edges
could still reach aggregate consumers where the graph says that is appropriate.

The correction is implemented in affected analysis. Focused validation and
Application tests cover subtree redirects, add/remove paths, both parents,
lineages, sibling exclusion, non-direct dispositions, readiness blocking, and
the normal reviewed atomic write path. The public lore walkthrough confirms an
edge-only Skyrim reparent now produces ten affected nodes, three context nodes,
and `readyBeforeReview: false`.

### 2. Make the modeling convention a product asset

Keep the new guidance in the CLI guide and consider a reviewed LoreProject
sample with roster, name, local-detail, scope-reframe, deletion, and reparent
goldens. This tests whether an authoring agent of the intended caliber naturally
builds the efficient pattern rather than a sibling clique.

### 3. Improve bounded discovery

A public immediate-scope-children query and a compact old/new scope-membership
summary would help authors reason about closed sets without reading all
descendant lore. Search already works well for candidate text consumers.

### 4. Consider an optional generic collection constraint later

If authoring/reviewer experiments still miss exact-count errors, add an explicit
optional collection/cardinality capability rather than hidden semantics for the
word `continent`. A collection declaration could identify a membership
relationship and an exact/minimum/maximum count. A deterministic validator could
then reject an unreconciled sixth member while permitting a transaction that
updates the declared count from five to six.

This would be a new profile or constraint contract. It should not be smuggled
into ordinary kinds, labels, or attributes, which are currently promised to have
no hidden common-engine semantics.

## SQLite, Lean, Metamath, and rule engines

SQLite remains the right authoritative store and transaction engine. The hard
parts exercised here are mutable graph state, bounded traversal, search, stale
state, and atomic replacement. Lean and Metamath are proof systems, not better
embedded mutable graph stores.

Formal proof is also a poor default semantic model for lore. Statements are
often incomplete, ambiguous, revised by authorial choice, or intentionally
inconclusive. Encoding all of that as formal propositions would cost far more
than the review it replaces and would falsely suggest that unformalized text is
proved consistent.

If deterministic rules grow beyond simple profile checks, a small Datalog-like
or constraint layer could complement the graph. Lean could later verify a truly
formal subdomain supplied by an optional profile. Neither should replace SQLite
or the human-readable graph for the general product.

## Recommendation

Retain the SQLite graph and the scope-plus-explicit-edge mental model. Adopt the
stable-scope, leaf-claim, directional-roster guidance. Do not add automatic
string dependencies or continent cliques.

Use the updated T12/T13 instructions to test whether the reviewer notices
closed-world contradictions and whether the authoring AI reliably creates and
maintains roster/name edges without sibling cliques. Defer a generic cardinality
profile until that evidence shows explicit modeling plus AI guidance is
insufficient.
