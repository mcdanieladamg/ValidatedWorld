# Privacy

ValidatedWorld is open-source local software published by Adam McDaniel. It has
no publisher-operated application server, user account, telemetry, advertising,
or built-in model API client. Its Python application does not upload project
documents or review evidence to the publisher.

## Project data and purpose

The software reads and changes project knowledge selected by you and your agent:
titles, claims, decisions, evidence, relationships, tags and typed attributes.
These records may contain personal information if you put it in your project.
Their purpose is documentation, retrieval and review of proposed changes.

The selected HTML document holds saved project data. Drafts, controller state,
proposals and complete protocol responses remain in process memory. For fresh
subagent review, the application writes exact evidence pages into one hidden
companion HTML beside the selected document, for example `.docs-vw.tmp.html`.
Reviewers read that file using ordinary host file access; the author receives
bounded findings and exact hash receipts. The application uses no reviewer
server, URL, native IPC channel or built-in messaging connection.

Saving replaces the companion's review contents with a verified candidate and
atomically replaces the selected HTML. Save, discard and graceful exit remove
owned review state. After a stopped process, a fresh session removes only
recognized abandoned companion data. Unrecognized or externally changed data is
preserved for inspection. Ending a process loses unfinished proposals and reviews.
No temporary scripts, response logs, working folders or automatic backups are
created. Explicit backups, conversion exports, optional DB authority and
host-captured command output may create additional copies. User-created documents
and copies remain until you delete or replace them.

## Agent hosts and recipients

Your agent reads project records and sends selected context to its reviewers.
The host may process this material locally or through its model providers and
connected services, and may retain chats, tool output or logs. Those recipients,
retention periods, usage charges and data controls depend on the host and your
configuration. Installing this skill does not change the host's policies.
Project folders may also be synchronized, backed up or committed through tools
you use separately. The publisher does not receive those copies through the
ValidatedWorld application.

## Your controls

Choose which project the agent can access, use the host's permission and data
controls, inspect proposed changes, and keep backups where appropriate. You can
inspect or delete your project documents and other retained local
copies. Uninstalling the skill does not delete project documents. Local deletion
does not delete host chat history, remote backups or version-control history;
manage those through their respective services.

Do not put credentials in the graph or include secrets or personal project data
in public support reports. If you choose to open a GitHub issue, GitHub receives
and handles that report under its own policies, and a public report can be read
by the publisher and other visitors. See [support](support.md) for a minimal,
sanitized reporting method. Privacy questions can use the same support channel.
