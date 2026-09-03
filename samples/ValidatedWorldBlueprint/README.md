# ValidatedWorld Blueprint Sample

This directory contains the generated, diff-friendly review projection of the
product's canonical self-blueprint.

- `baseline.json`: a fingerprint-bound snapshot containing 146 nodes and 248
  explicit edges exported from `ValidatedWorld.Blueprint.vw.db`.
- The corresponding inspectable SQLite file is
  `ValidatedWorld.Blueprint.vw.db` in the repository root. That database is the
  tracked source of truth; this JSON must not be edited independently.

Regenerate and verify the projection from the repository root:

```powershell
./scripts/export-blueprint.ps1
./scripts/export-blueprint.ps1 -Check
```

From the repository root:

```powershell
dotnet run --no-restore --project src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj -- project verify ValidatedWorld.Blueprint.vw.db
dotnet run --no-restore --project src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj -- read search ValidatedWorld.Blueprint.vw.db "AI review" --limit 20
dotnet run --no-restore --project src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj -- read tag ValidatedWorld.Blueprint.vw.db "area:ai-review" --limit 20
dotnet run --no-restore --project src/ValidatedWorld.Cli/ValidatedWorld.Cli.csproj -- shell ValidatedWorld.Blueprint.vw.db
```

The database began through the overly broad public `project.init`
path, so it is evidence for representation and browsing, not for a safe
new-world review boundary. T15 will add a repeatable piecewise builder that
starts with the purpose root and commits scope-sized batches through ordinary
change sessions. It must not submit this projection or a complete project to
AIReview.
