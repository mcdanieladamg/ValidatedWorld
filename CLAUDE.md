# Claude repository instructions

Read and follow `/AGENTS.md`. `/README.md` is the product introduction and
bootstrap. `/ValidatedWorld.Blueprint.vw.db` is the detailed product design,
implementation contract, current phase, and ordered roadmap.

Implement only the blueprint phase tagged `status:current`, run its automated
and smoke checks, update phase state in the reviewed blueprint on success,
report to the human, and stop. Keep documentation current-state only rather than
appending dated development history. Do not perform Git
state-changing or remote operations and do not launch another agent.
