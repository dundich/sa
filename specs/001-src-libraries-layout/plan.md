# Plan — SPEC-001

## Move list

`src/<Name>/` → `src/Libraries/<Name>/` for all 17 library projects:

`Sa`, `Sa.Configuration`, `Sa.Configuration.PostgreSql`, `Sa.Data.PostgreSql`, `Sa.Data.S3`,
`Sa.Data.TempFolder`, `Sa.HybridFileStorage`, `Sa.HybridFileStorage.FileSystem`,
`Sa.HybridFileStorage.Postgres`, `Sa.HybridFileStorage.S3`, `Sa.Media`, `Sa.Media.FFmpeg`,
`Sa.Outbox`, `Sa.Outbox.PostgreSql`, `Sa.Partitional.PostgreSql`, `Sa.Schedule`,
`Sa.Utils.WorkQueue`.

Stays in place: `src/Samples/`, `src/Tests/`, `src/Benchmarks/`, `src/*.props`, `src/*.xml`,
`src/global.json`, `src/nuget.config`, `src/Sa.slnx`.

## Reference edits (nothing else)

| Reference | From | To |
|---|---|---|
| Library props import | `../Common.NuGet.Properties.xml` / `../Common.Properties.xml` | `../../…` |
| Packed readme/icon | `..\..\docs\NuGet.md`, `..\..\icon.png` | `..\..\..\…` |
| Solution entries | `Sa.Outbox/Sa.Outbox.csproj` | `Libraries/Sa.Outbox/…` |
| Samples/tests project refs | `..\..\Sa.X\Sa.X.csproj` | `..\..\Libraries\Sa.X\…` |
| Fixture project refs | `..\..\..\Sa.X\Sa.X.csproj` | `..\..\..\Libraries\Sa.X\…` |
| Pack path (`tasks.sh`, `tasks.ps1`) | `$ROOT/src/$project/$project.csproj` | `$ROOT/src/Libraries/$project/…` |
| Package check | `$SRC_DIR/Sa.Media.FFmpeg/…` | `$SRC_DIR/Libraries/Sa.Media.FFmpeg/…` |
| CI line-ending fix | `src/Sa.Media.FFmpeg/build/*.sh` | `src/Libraries/Sa.Media.FFmpeg/build/*.sh` |
| Docs | every `src/Sa.*` / `src/Sa` link | `src/Libraries/…` |

Libraries referencing each other and `<Compile Include="..\Sa\...">` links do **not** change: both
sides move together, so relative paths stay valid.

## Steps

1. Move the 17 directories with `git mv` (history preserved). ✅
2. Rewrite the reference set above (no other edits). ✅
3. `dotnet build src/Sa.slnx -c Release` — expect 0 warnings, 0 errors. ✅
4. `./build-sh/do-test.sh` — compare to `specs/000-project-baseline/baseline.md`. ✅
5. `./build-sh/do-package.sh` — compare package list/versions/metadata. ✅
6. `./build-sh/do-package-check.sh`. ✅
7. Update `docs/repository/project-structure.md` and mark stage 3 in
   `docs/development/migration-plan.md`. ✅
8. Verify all relative links and `git status` (no unexpected file changes). ✅

## Risks

- A missed relative reference fails at restore/build, not at runtime — build-time detection is good,
  but `dotnet test`/`pack` paths (`tasks.sh`, `package-check.sh`) are only exercised by running them.
- `..\..\` depth errors are easy to make in fixtures (`src/Tests/Fixtures/<X>/` is three levels deep).
- MSBuild/NuGet discovery of `src/Directory.Build.props`, `src/Directory.Packages.props` and
  `src/nuget.config` depends on the files staying in `src/` — they do.
- Line-ending normalization in CI rewrites `*.sh` under the FFmpeg build folder; a stale path there
  would silently stop normalizing, so it is updated explicitly.

## Rollback

`git revert` the move commit. No runtime or data migration is involved, and no version was bumped, so
rollback cannot leave published artifacts inconsistent.
