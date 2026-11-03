# Plan — SPEC-003

## Move list

| From `src/Libraries/` | To `src/Libraries/` |
|---|---|
| `Sa.Configuration`, `Sa.Configuration.PostgreSql` | `Configuration/` |
| `Sa.Data.PostgreSql`, `Sa.Data.S3`, `Sa.Data.TempFolder` | `Data/` |
| `Sa.HybridFileStorage`, `Sa.HybridFileStorage.FileSystem`, `Sa.HybridFileStorage.Postgres`, `Sa.HybridFileStorage.S3` | `Hybrid/` |
| `Sa.Media`, `Sa.Media.FFmpeg` | `Media/` |
| `Sa.Outbox`, `Sa.Outbox.PostgreSql` | `Outbox/` |
| `Sa`, `Sa.Partitional.PostgreSql`, `Sa.Schedule`, `Sa.Utils.WorkQueue` | stay at the root |

## Steps

1. `git mv` the 13 directories into their group folders.
2. Rewrite relative references in every `.csproj` by resolving each existing `ProjectReference`,
   `Compile Include` and `Import` against the filesystem and re-emitting the path from the project's
   new directory (avoids hand-editing 17×N entries and cannot leave a stale `..`).
3. Rewrite `src/Sa.slnx`: grouped paths plus `/Libraries/<Group>/` solution folders.
4. Make packaging group-agnostic: `build-sh/tasks.sh` and `build/tasks.ps1` locate
   `<Project>.csproj` under `src/Libraries/` instead of assuming `src/Libraries/<Project>/`.
   Update `build-sh/package-check.sh` (Media group) and the CI line-ending path.
5. Update `.gitignore` (`Media/Sa.Media.FFmpeg`, `Media/Sa.Media`).
6. Update live docs: `README.md`, `README-ru.md`, `AGENTS.md`,
   `docs/repository/project-structure.md`, `docs/architecture/target-architecture.md`,
   `docs/development/migration-plan.md`. `specs/001`/`002` stay as historical records.
7. Verify against `specs/000-project-baseline/baseline.md`.

## Verification

- `bash -n` on all `build-sh/*.sh`.
- Every `ProjectReference`/`Compile Include`/`Import` resolves on disk.
- `dotnet sln src/Sa.slnx list` → 48 projects.
- `do-build.sh`, `do-test.sh`, `do-package.sh`, `do-package-check.sh`.
- Link check over all Markdown.

## Risks

- Depth change (one extra level) is the whole risk: a missed `..` fails at restore/build, and
  packaging paths fail only when `do-package.sh` actually runs — hence both are exercised.
- `dotnet clean` after the move removes outputs from the previous paths only when invoked; stale
  `bin/obj` in old locations are ignored by git and are removed explicitly.
- Solution-folder nesting must keep the `Configuration.Web` project Id intact.

## Rollback

One revert. No version bump, no published artifact, no schema change.
