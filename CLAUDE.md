# Claude repository instructions

Read and follow `/AGENTS.md`. The product authority is `/README.md`; the
technical source of truth is `/docs/technical_design.md`; and the single current
assignment and ordered backlog are in `/docs/development_plan.md`.

Implement only Current task, run its automated and smoke checks, update the plan
state on success, report to the human, and stop. Keep documentation current-state
only rather than appending dated development history. Do not perform Git
state-changing or remote operations and do not launch another agent.
