# ValidatedWorld Blueprint Sample

This directory contains the tracked semantic source for the product's
self-blueprint.

- `baseline.json`: 134 nodes and 219 explicit edges exported from
  `ValidatedWorld.Blueprint.vw.db`.
- The corresponding inspectable SQLite file is
  `ValidatedWorld.Blueprint.vw.db` in the repository root. The binary is
  intentionally ignored by Git; this JSON keeps its content reviewable in
  ordinary diffs.

From the repository root:

```powershell
dotnet run --no-restore --project src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj -- project verify ValidatedWorld.Blueprint.vw.db
dotnet run --no-restore --project src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj -- read search ValidatedWorld.Blueprint.vw.db "AI review" --limit 20
dotnet run --no-restore --project src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj -- read tag ValidatedWorld.Blueprint.vw.db "area:ai-review" --limit 20
dotnet run --no-restore --project src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj -- shell ValidatedWorld.Blueprint.vw.db
```

The current database began through the overly broad public `project.init`
path, so it is evidence for representation and browsing, not for a safe
new-world review boundary. T15 will add a repeatable piecewise builder that
starts with the purpose root and commits scope-sized batches through ordinary
change sessions. It must not submit this baseline or a complete project to
AIReview.
