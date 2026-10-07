# ValidatedWorld

ValidatedWorld helps AI agents maintain a graph of project knowledge and reason
consistently across projects larger than their context window. It tracks decisions
and dependencies, retrieves targeted context, and can fan out reviews to batches
of fresh, zero-context sub-agents so proposed changes receive focused, independent
scrutiny before they propagate through the project. The objective is graph-wide
conceptual consistency: not an absolute guarantee, but a systematic attempt to
detect and resolve inconsistencies throughout the connected knowledge base,
managed by your AI agent.

The graph is stored in your project as a human-friendly HTML file that you can open in a browser
to read and explore.

See the bundled `document_format.md` and the
[repository](https://github.com/mcdanieladamg/ValidatedWorld) for installation,
agent instructions and optional command details. Bundled [privacy](docs/privacy.md),
[software terms](docs/terms.md) and [support](docs/support.md) explain data handling,
licensing and reporting.
