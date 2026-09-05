# Repository instructions

Read and follow `/AGENTS.md`. The product authority is `/README.md`; the
detailed product knowledge, implementation state, single current assignment,
and ordered backlog are in `/ValidatedWorld.Blueprint.vw.db`.

Implement only the blueprint phase tagged `status:current`, run its automated
and smoke checks, update the phase state on success, and review the semantic
database diff beside the source diff.
Every successful phase implementation includes a blueprint delta that records
delivery even when its meaning was already planned. Only work that changes
neither meaning nor recorded delivery state may omit the database, and that
exception must be reported. Report to the human and stop. Keep documentation
current-state only rather than appending dated development history. Do not
perform Git state-changing or remote operations and do not launch another agent.
