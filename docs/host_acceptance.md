# Release verification

The skill requires Python 3.11+, project-file access, persistent processes and
fresh read-only subagents for reviewed updates. Compatibility is described by
these capabilities rather than an operating-system or host allowlist.
The same bundled standard-library engine ships in the
standalone skill and skills-only plugin.

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
verification passed 178 units with 90.68% branch coverage, both rebuilt archive
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
