# Acceptance criteria — SPEC-003

Verified 2026-10-30 on the working tree after SPEC-001/002 (branch `sdd`).

- [x] **AC-1** — 13 domain projects live in `Configuration/`, `Data/`, `Hybrid/`, `Media/`, `Outbox/`;
  the 4 standalone projects stay directly in `src/Libraries/`; no project, assembly or namespace was
  renamed.
  Evidence: `find src/Libraries -maxdepth 1 -type d` → `Configuration`, `Data`, `Hybrid`, `Media`,
  `Outbox`, `Sa`, `Sa.Partitional.PostgreSql`, `Sa.Schedule`, `Sa.Utils.WorkQueue`. `git status`
  reports every moved path as a rename (`R`), so directory names are unchanged; `Sa.slnx` has 48
  `<Project>` entries.

- [x] **AC-2** — Every path-dependent reference resolves: props imports, `ProjectReference`s,
  `<Compile Include>` links, samples/tests/benchmarks/fixtures references, packaging paths, CI and
  `.gitignore`.
  Evidence: scripted resolution of every `ProjectReference` / `Compile` / `Import` in all `.csproj`
  → `unresolved 0`. `Common.NuGet.Properties.xml` now resolves the packed `docs/NuGet.md` and
  `icon.png` from `$(MSBuildThisFileDirectory)` so the same file works at any project depth.
  `.gitignore` matches `src/Libraries/Media/Sa.Media.FFmpeg/...` (`git check-ignore` confirms).

- [x] **AC-3** — `dotnet build` matches the baseline: 0 warnings, 0 errors, 48 projects.
  Evidence: `./build-sh/do-build.sh` → exit `0`, `Build succeeded`, `0 Warning(s)`, `0 Error(s)`.

- [x] **AC-4** — Test totals match the baseline.
  Evidence: `./build-sh/do-test.sh` → exit `0`, `total: 2712`, `succeeded: 2711`, `skipped: 1`,
  `failed: 0` — identical to `specs/000-project-baseline/baseline.md`.

- [x] **AC-5** — Packages are unchanged: 16 IDs at the same versions, 16 `.snupkg`.
  Evidence: `./build-sh/do-package.sh` → exit `0`, 16 `.nupkg` + 16 `.snupkg`; versions are 15 ×
  `0.17.0` and `Sa.Partitional.PostgreSql` `0.18.0`. Each `.nupkg` still contains `NuGet.md` and
  `icon.png` (`unzip -l`).

- [x] **AC-6** — `do-package-check.sh` is green.
  Evidence: exit `0`, `Package payload OK (0.17.0)`.

- [x] **AC-7** — The solution groups libraries by domain.
  Evidence: `dotnet sln src/Sa.slnx list` lists all 48 projects; solution folders
  `/Libraries/`, `/Libraries/{Configuration,Data,Hybrid,Media,Outbox}/` are declared in
  `src/Sa.slnx` (flat declarations — nested `<Folder>` elements are not read by the SDK,
  which silently dropped the 13 grouped projects from `dotnet sln list`).

- [x] **AC-8** — Documentation matches the tree and has no broken links.
  Evidence: `README.md`, `README-ru.md`, `AGENTS.md`, `docs/repository/project-structure.md`,
  `docs/architecture/target-architecture.md`, `docs/development/migration-plan.md` and
  `build-sh/readme.md` point at the grouped paths; a link check over all Markdown outside
  `specs/000…002` found 0 broken relative links.

- [x] **AC-9** — Packaging no longer hard-codes a group.
  Evidence: `nu_pack()` in `build-sh/tasks.sh` (and `NuPack` in `build/tasks.ps1`) locates
  `<Project>.csproj` under `src/Libraries/` and fails when it is missing; `package-check.sh` uses
  `Libraries/Media/Sa.Media.FFmpeg`.

## Regression checks

- [x] `bash -n` on every `build-sh/*.sh` — no syntax errors.
- [x] `./build-sh/do-build.sh` — exit `0`, 0 warnings / 0 errors.
- [x] `./build-sh/do-test.sh` — exit `0`, 2712 / 2711 / 1 / 0.
- [x] `./build-sh/do-package.sh` — exit `0`, 16 + 16 packages, versions unchanged.
- [x] `./build-sh/do-package-check.sh` — exit `0`.
- [x] Markdown link check — 0 broken links (excluding historical `specs/000`–`specs/002`).
- [x] No functional, formatting or version change mixed into the move.
