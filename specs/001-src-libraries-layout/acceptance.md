# Acceptance criteria — SPEC-001

Verified against the baseline from `specs/000-project-baseline/baseline.md` on 2026-10-30.

- [x] **AC-1** — All 17 library projects live under `src/Libraries/<ProjectName>/`; no library
  directory remains directly under `src/`.
  Evidence: `ls src` → `Libraries`, `Samples`, `Tests`, `Benchmarks`, `Sa.slnx`, shared props/config;
  `ls src/Libraries | wc -l` → 17.

- [x] **AC-2** — Project/assembly/package names are unchanged: 16 package IDs at the same versions,
  `net10.0` for all projects.
  Evidence: `ls dist/*.nupkg` → the same 16 IDs; all `0.17.0` except `Sa.Partitional.PostgreSql`
  `0.18.0`; no `<TargetFramework>` outside `net10.0`.

- [x] **AC-3** — No path-dependent reference points at an old location.
  Evidence: every `ProjectReference` in `src/Samples`, `src/Tests`, `src/Benchmarks` resolves on disk;
  `src/Sa.slnx` lists `Libraries/…`; `src/Common.NuGet.Properties.xml` uses `..\..\..\docs\NuGet.md`
  and `..\..\..\icon.png`; `build-sh/tasks.sh`, `build/tasks.ps1`, `build-sh/package-check.sh` and
  `.github/workflows/dotnet.yml` point at `src/Libraries/…`; `.gitignore` ignores
  `src/Libraries/Sa.Media.FFmpeg/{build/*.tar.gz,build/artifacts,build/build.*,sa/native}` and
  `src/Libraries/Sa.Media/…` payload paths.

- [x] **AC-4** — `./build-sh/do-build.sh` succeeds with 0 warnings / 0 errors (48 projects).
  Evidence: `Build succeeded. 0 Warning(s), 0 Error(s)`.

- [x] **AC-5** — `./build-sh/do-test.sh` matches the baseline totals.
  Evidence: `total: 2712`, `succeeded: 2711`, `skipped: 1`, `failed: 0`, `4m 06s` — identical to the
  pre-move run.

- [x] **AC-6** — `./build-sh/do-package.sh` produces 16 `.nupkg` + 16 `.snupkg` with `NuGet.md` and
  `icon.png` embedded.
  Evidence: 16/16 files in `dist/`; `unzip -l dist/Sa.Outbox.0.17.0.nupkg` shows `NuGet.md`,
  `icon.png`, `lib/net10.0/Sa.Outbox.dll`.

- [x] **AC-7** — `./build-sh/do-package-check.sh` succeeds.
  Evidence: exit `0`, consumer prints `ffmpeg version 7.1.5`, `Package payload OK (0.17.0)`.

- [x] **AC-8** — No source, dependency, version or formatting change is mixed in.
  Evidence: `git diff --diff-filter=M` touches 56 files, **0** of them `.cs`; every `.csproj` diff is
  a path line; `git status` reports 598 pure renames and 17 rename+path-edit. Version and
  `PackageReference` lines are untouched.

- [x] **AC-9** — Every relative link in `README.md`, `README-ru.md`, `AGENTS.md`, `docs/**`,
  `specs/**` and the moved library readmes resolves.
  Evidence: scripted link check over all Markdown → 0 broken links (after fixing
  `src/Libraries/Sa.Schedule/Readme*.md`, which pointed at `../Samples/…`).

## Package identity snapshot (post-move)

| Package | Version |
|---|---|
| `Sa.Configuration` | 0.17.0 |
| `Sa.Configuration.PostgreSql` | 0.17.0 |
| `Sa.Data.PostgreSql` | 0.17.0 |
| `Sa.Data.S3` | 0.17.0 |
| `Sa.Data.TempFolder` | 0.17.0 |
| `Sa.HybridFileStorage` | 0.17.0 |
| `Sa.HybridFileStorage.FileSystem` | 0.17.0 |
| `Sa.HybridFileStorage.Postgres` | 0.17.0 |
| `Sa.HybridFileStorage.S3` | 0.17.0 |
| `Sa.Media` | 0.17.0 |
| `Sa.Media.FFmpeg` | 0.17.0 |
| `Sa.Outbox` | 0.17.0 |
| `Sa.Outbox.PostgreSql` | 0.17.0 |
| `Sa.Partitional.PostgreSql` | 0.18.0 |
| `Sa.Schedule` | 0.17.0 |
| `Sa.Utils.WorkQueue` | 0.17.0 |
