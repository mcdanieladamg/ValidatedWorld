# ValidatedWorld CLI installation

This archive contains the self-contained Windows x64 CLI. It does not require a
.NET SDK or runtime.

Workflows are supported in English only. Graph text supports Unicode storage
and round-tripping.

1. Extract the complete archive to a user-owned local directory. Paths with
   spaces are supported.
2. Run `ValidatedWorld.Cli.exe --version` and confirm it reports `{{VERSION}}`.
3. Run `ValidatedWorld.Cli.exe --help` or consult the
   [CLI reference](https://github.com/mcdanieladamg/ValidatedWorld/blob/main/docs/cli_usage.md).

Keep `.vw.db` projects, backups, credentials, and settings outside this folder.
Upgrade by extracting a newer complete archive to a new folder, verify its
version, then replace the old application folder. Uninstall by removing only the
application folder.

For an installed binary without the .NET SDK, configure optional review through
`VW_AIREVIEW__*` or `OPENAI_API_KEY` process/user environment variables. Store
credentials in the process or user environment rather than the archive or a
`.vw.db` project.
