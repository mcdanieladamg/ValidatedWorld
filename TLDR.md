ValidatedWorld is a local AI agent plugin, built using the OpenAI Plugin format, that lets an agent maintain any project’s semantic knowledge as a pre-validated connected model, then review and save the consequences of proposed changes in a structured way—preserving stronger semantic validation over time than freeform changes alone—all in a single local SQLite database file.

The initial public release is supported in English only. Graph text uses Unicode
and can store and round-trip other languages, but the product instructions,
diagnostics, bundled content, ranked-search tuning, and optional AI workflows are
authored and validated only in English; non-English workflow quality is not
currently supported.

```powershell
# To install as an OpenAI Plugin for use locally (current instructions install through a local marketplace as we are not publicly listed yet)

# Build/test/package — repository root, clean commit, Windows x64, .NET 10 + Codex host
.\eng\Prepare-LocalPlugin.ps1

# Install/update — use the output from the command above, with the version printed, i.e.
.\eng\Install-LocalPlugin.ps1 -Version <printed-version>
```

```text
Restart the ChatGPT Desktop or Codex app after plugin install, start a new agent chat, and instruct it to build or modify the db file with commands such as:

Use ValidatedWorld to report host_status.

Use ValidatedWorld to create <absolute-path>.vw.db for <project and purpose>.

Use ValidatedWorld to open <absolute-path>.vw.db and summarize its purpose.

Use ValidatedWorld to make <requested change>; review affected knowledge and save it.
```
