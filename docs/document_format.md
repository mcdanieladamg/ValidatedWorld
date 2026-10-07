# Documentation representation

`validated-world-document-2` is one UTF-8 HTML file with LF line endings.
Prefer `docs-vw.html`; explicit custom filenames identify independent projects.
A human opens the file directly in a browser with JavaScript enabled. A compact,
versioned inline viewer reads the embedded data; no external assets, server or
browser network requests are required.

## Data and presentation

Exactly one `<script type="application/json" id="vw-record">` block contains
the complete project object, including inline node and edge arrays. This passive,
pretty-printed JSON is the sole import authority. Import ignores the surrounding
HTML, CSS and viewer code, accepts compatible data edits, and fails on incompatible
records. Import never executes JavaScript or audits changes against the last export.
Export emits the fixed viewer and data, without pre-rendered graph markup.

The root node appears first. Project metadata, node/edge inventories, tag index,
node records and edge records become linked sections in the same document. Scope
breadcrumbs, children and relationship endpoints link to section anchors.
`purpose`, `project` and `tags` identify the root and indexes; other record anchors
are `node:<id>` and `edge:<id>`, using the exact opaque case-sensitive entity ID.
Link fragments percent-encode the entire anchor with JavaScript `encodeURIComponent`.
IDs remain exact data, never filesystem paths. Initial fragment navigation runs
after the viewer creates its targets; ordinary browser links and history work normally.

The viewer version is independent of the data format marker.
`<meta name="vw-viewer-version" content="4">` is ignored by import.
The viewer capitalizes paragraph starts and labels; raw IDs, tags, enums, typed
values and imported graph text retain exact case. Visible claims derive from
JSON and do not become a second authority. The viewer uses DOM construction and
text nodes rather than interpreting graph values as markup. The document's
content policy permits only the exact inline viewer by SHA-256, blocks external
resources and allows the small inline stylesheet. Browser policies may still
block JavaScript; browsing requires its execution.

## Compatibility

Version 1.0.0 establishes `validated-world-document-2` as the supported project
document format. Later 1.x releases must continue to read and update valid 1.x
documents while preserving graph meaning, IDs and typed values. Viewer changes
may regenerate presentation without changing this data contract. An incompatible
document or explicit DB-authority schema change requires a new major application
version and documented conversion instructions; it must not silently reinterpret
existing projects. The document format marker and viewer version are independent
of the application version. Version 1.0.0 includes no legacy reader or automatic
format upgrade, and does not promise compatibility with earlier development
formats.

Export stages and verifies one sibling file before atomically replacing the
selected `.html` destination. Neighbors are untouched. New projects and backups
are non-overwriting. Failed publication after the reviewed SQLite commit retains
the temporary DB for `project retry-export`; successful publication precedes its
managed cleanup. Explicit caller-owned DBs are never automatically deleted.

New-file publication uses a same-directory no-overwrite rename on Windows,
without creating hard links, for HTML creation/backups and SQLite initialization/
backups. POSIX uses hard-link publication because its ordinary rename can replace
an existing file. Both refuse a destination created by another writer after
preflight; existing-document updates continue to use atomic replacement.

HTML initialization, reads, SQL export and managed changes allocate unique SQLite
workspaces under `tmp/validated-world/` in the selected document's parent,
independent of the process's OS-temp configuration or working directory.
The shared parent is created if needed and remains after cleanup. Linked parents
and collisions with existing files are rejected. The project must be writable
even for reads. Allocation failure reports its path and does not fall back elsewhere.
Ordinary completion, discard and graceful shutdown remove owned workspaces;
explicit retention and committed unpublished results preserve their reported DBs.
Session response logs and default packet-review exports use the same shared
parent. Cleanup removes only owned operation subfolders, preserving other live
sessions and unknown files. Atomic publication staging files and lock sidecars
remain beside the document until their operation finishes.

## JSON fields

All listed members are required; unknown and duplicate members fail parsing.

| Record | Members, in emitted order |
| --- | --- |
| Project | `format`, `id`, `title`, `purpose`, `created`, `updated`, `nodes`, `edges` |
| Node | `id`, `text`, `kind`, `tags`, `attributes` |
| Edge | `id`, `source`, `target`, `relationship`, `reviewDirection`, `rationale`, `tags`, `attributes` |
| Attribute | `name`, `type`, `value` |

Project `format` is `validated-world-document-2`; `purpose` is a node ID.
`nodes` and `edges` are arrays of inline node and edge objects.
`created` and `updated` are UTC timestamps such as
`2026-10-04T10:00:00.0000000+00:00`. Conversion preserves them exactly; only a
committed semantic change updates `updated`.

`kind` and `rationale` may be JSON null or strings; null and an empty string differ.
`tags` is an array of strings. `attributes` is an array of typed attribute records.
Other graph fields are strings. `reviewDirection` is `none`, `sourceToTarget`,
`targetToSource` or `both`; it describes consequence review independently of
the relationship's source/target direction.

| Attribute type | JSON value |
| --- | --- |
| `text` | String, with exact whitespace and Unicode |
| `integer` | Canonical signed decimal **string**, within signed 64-bit range |
| `decimal` | Canonical decimal string, using existing graph scalar rules |
| `boolean` | JSON `true` or `false` |
| `symbol` | String |
| `instant` | Canonical UTC timestamp string |

Integer strings avoid numeric precision loss in independent readers. Ordinary
JSON escapes preserve controls, quotes and backslashes. Export additionally
escapes `<`, `>` and `&` as `\u003c`, `\u003e` and `\u0026`, preventing graph text
from closing the data block or introducing markup. Display escapes exceptional
control characters visibly; the JSON still preserves their exact values.

Records use canonical graph ordering and fixed pretty JSON field order.
No-op round trips with the same renderer produce identical bytes. One claim edit
changes only its JSON line and the JSON update timestamp. Viewer, policy hash,
stylesheet and HTML shell are independent of graph values; structural edits affect
only their JSON records. Independent tools can extract records
with an HTML tokenizer and a JSON decoder; they should retain typed values and
apply the normal graph validity and rule contracts before saving changes.
