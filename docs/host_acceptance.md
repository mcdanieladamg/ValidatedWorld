# Release verification

The skill requires Python 3.11+, project-file access, persistent processes and
fresh read-only subagents for reviewed updates. Compatibility is described by
these capabilities rather than an operating-system or host allowlist.
The same bundled standard-library engine ships in the
standalone skill and skills-only plugin.

## Current replacement workflow

Version 1.0.3 now uses bundled `Session` and `ReviewReader` helpers, ordinary
reviewer file reads and a single hidden companion HTML. URL/native/message
handoffs and earlier no-companion claims below describe superseded builds.
Current Windows verification covers complete responses, automatic references,
full receipt/binding validation, one-companion write audits, cleanup and atomic
replacement failures. Local replacement verification passed 180 unit tests with 90.04% branch coverage,
developer/blueprint checks, both isolated candidate archive workflows and Windows
temporary-path alias checks. An exact standalone candidate trial authored 45 nodes
and 81 edges, read all 126 affected/context items across 26 pages, and blocked
saving before independent review. Three simultaneous fresh no-history read-only
subagents read 19, 18 and 18 bounded pages from the same frozen companion; its
bytes stayed unchanged throughout their reads. A separate fresh synthesis read
both complete global pages and all branch results. Their unchanged bound replies
and verified receipts enabled saving, verification and read-back. Graceful close
left only `docs-vw.html`. The companion was generated in code from RAM (227,322
bytes for the branch evidence); packet bodies and private reviewer histories did
not pass through the author's context.

Browser-only preview could not complete on this host: sandbox sockets were
denied, and the supported local preview still timed out after the diagnostic
retry outside the sandbox. No security settings were changed. This limits the
interactive browser smoke result; the reviewer workflow used ordinary file reads
and passed without network access. Browser/viewer unit regressions passed, and
the canonical update preserved the exact HTML shell/viewer bytes. Linux and macOS
execution remains CI work.

A maintainer-reported external Windows trial initially failed shell startup for
all skills, including built-in skills. Rebooting reportedly restored startup.
A subsequent run initialized/verified HTML and discovered impact, but a separate
reviewer rejected a native channel name and the message alternative failed to
deliver evidence. The proposal was discarded and the saved HTML remained purpose
only. The exact cause is unavailable; this replacement removes both mechanisms.
Free-tier status alone does not establish the reported failure's cause.

## Historical verification

| Evidence | Host | Result |
| --- | --- | --- |
| Maintainer-reported external computer A | Codex desktop on Windows, ChatGPT Plus account | Skill installation and project workflow reported successful. |
| Maintainer-reported external computer B | Codex desktop on Windows, ChatGPT Free account | Skill installation and the requested create, verify, purpose-read and small reviewed-update trial reported successful. |

These reports were accepted for release preparation on 2026-10-06. They concern
the development skill preceding 1.0.0; this release changes version and release
documentation, without changing the graph engine or session helper. Exact
desktop build, model and complete per-command transcripts were not supplied.
The reports are human evidence, not independently replayed measurements.
Account identifiers and personal project data are intentionally excluded.

Local verification of both final 1.0.0 archives on Windows used the Codex bundled
Python 3.12.14 runtime in disposable code and garden projects. Initialization,
retrieval, a blocked write without approval, an actual fresh no-history subagent
review, publication and read-back verification passed. Graceful shutdown left
only each project's `docs-vw.html`, removing the owned logs and working databases.
The generated garden document also passed a loopback browser check for readable
claims, child and relationship navigation, endpoint links and deep-link reload,
with no console or CSP errors. This was a local check, separate from the two
external-machine reports and normal human file opening.

Use [developer verification](developer_testing.md) to reproduce the public,
isolated-package and fresh-reviewer smoke methods. Offline checks separately
exercise the shipped source, exact-review binding failures, batch and worker
contracts, document publication/recovery and cleanup. Synthetic approvals in
offline tests do not replace a real fresh host reviewer. Optional worker and
recovery scenarios were not individually reported on both external computers.

Agent hosts control model availability and account features. Use GitHub Issues
to report compatibility problems with a small sanitized example. Platform CI
results and public-directory approval are separate evidence.

Version 1.0.1 changes compatibility wording and version metadata. Both rebuilt
archives passed the isolated Python 3.11 package workflows, live response-log
author/review/save sequence, Windows hard-link restriction and temporary-path
alias checks. All five package-metadata tests passed. Archive hashes, staged
source and packaged engine bytes were checked against the current checkout.

The in-memory 1.0.2 candidate was exercised on 2026-10-07 with Python 3.11.
Both archives passed isolated author/review/save workflows through native pipes
and the optional local controller, under an audit hook denying every filesystem
write except the selected HTML. A separate live garden trial used two fresh
no-history branch reviewers and one fresh synthesis reviewer. Each retrieved
bounded, hashed packets through read-only in-memory endpoints; their unchanged
allow results enabled the exact reviewed save. Verification and read-back passed,
and the trial contained only its selected `garden.html`. Browser inspection passed
purpose-first display, child and relationship navigation, endpoint links and
deep-link reload with no console or CSP errors. Full units passed 166 tests with
90% branch coverage. These local checks are separate from the earlier external
machine reports. Direct HTML saving can leave a partial file when interrupted;
unsaved work and approvals are intentionally discarded with the process.

Version 1.0.3 removes reverse-DNS resolution from both optional loopback servers.
DNS-denied regressions failed before the correction and passed afterward. Local
verification passed 179 units with 90.68% branch coverage, both rebuilt archive
workflows and temporary-path alias checks. Persistent sessions now confine file
access to one launch-selected project folder and its subfolders. Regressions
reject outside reads/writes, root expansion, links/junctions and Windows aliases;
inside-root templates, backups and normal authoring remain usable. A separate
candidate-launcher garden trial blocked saving without host approval, accepted
an actual fresh no-history reviewer's unchanged allow decision, saved and verified
the change, and left only the selected HTML after shutdown. Browser inspection
confirmed readable saved text, record navigation and deep-link reload with no
console or CSP errors. The startup tests retain their 30-second budget and report
child exit status/stderr. CodeQL alert clearance requires the next CI analysis.

An additional maintainer-reported trial on a plain Windows computer successfully
used retained Python subprocess pipes after an unsuccessful attempt to introduce
a separate process manager. The skill now makes that Python recipe the preferred
method. An executable-documentation regression runs the actual published recipe
through Unicode authoring, review, guarded saving, verification and shutdown;
it passed locally with Python 3.11 and 3.12. This regression uses an offline
approval fixture, distinct from the fresh host-review trials above.

A follow-up report from that computer found strict path resolution denied even
though launching in the selected folder succeeded. The recipe now uses ordinary
non-strict resolution. The reported startup, root confirmation and clean shutdown
passed with that change; document creation was not exercised in that follow-up.
The executable-recipe regression reproduces strict-preflight denial and verifies
the complete author/review/save workflow locally. Application confinement checks
are unchanged.

A subsequent maintainer report from that plain Windows computer described
reviewer loopback failures (socket access denied, then connection timeout) and a
successful compressed-file handoff. The exact isolation or permission cause
was not established. Reviewers independently read complete packets; the author
received progress and decisions rather than their private tool histories.
The author also printed overly large controller responses. The file handoff
used in that trial was subsequently rejected by the maintainer because ordinary
skill use must alter only the selected HTML. Those historical results do not
establish acceptance of the current in-memory message route or cross-machine
loopback reachability.

A later external report described a restarted controller, session-reference
mismatch and KeyError: 'payload' before saving or visibly launching reviewers.
Direct change.show and all 26 affected pages reportedly worked; the unsaved
proposal had 45 nodes and 81 edges while the saved HTML retained its purpose.
The controller code and traceback are unavailable. A payload-only wrapper is a
plausible explanation, reproduced by a local regression, not a diagnosis of that
external run. The current helper validates the full NDJSON envelope before
accessing payload; restarting a controller still discards its proposal and
requires a fresh session against verified saved data.

The corrected 1.0.3 standalone candidate was extracted and exercised locally on
Windows with Python 3.11.3. Four fresh no-history branch reviewers fetched all
24 assigned native-channel pages; a separate fresh synthesis reviewer fetched
all five global pages and reconciled the four branch conclusions. Each verified
exact raw hashes, binding and complete pagination. Their unchanged cited allows
enabled the guarded save; missing branch/synthesis decisions blocked saving.
Verification, Unicode read-back, affected-export completeness and post-save
revocation passed. Shutdown left only the selected HTML. Sandbox pipe reads
were denied; narrowly approved read-only calls reached the named pipe. This
establishes delivery with that host permission, not universal sandbox access.
Browser checks passed purpose-first display, children, breadcrumbs, relationships,
endpoints, tags, deep-link reload, narrow-view readability and saved Unicode text
with no console/CSP errors. The Windows suite passed 183 tests at 90.24% branch
coverage; both archives and the temporary-path alias regression passed.
Linux/macOS execution and security analysis remain CI checks. Native IPC does
not cross inaccessible kernels; exact messages still consume real host context,
and refinement cannot eliminate coherent synthesis obligations.
