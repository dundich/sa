# Baseline measurements — SPEC-000

> **Layout note.** This baseline was measured on commit `2b422c9`, before SPEC-001 moved the libraries
> into `src/Libraries/`. The results below remain the reference for build/test/package behavior; only
> project paths changed. Package IDs, versions and TFMs are unaffected.

Recorded for the documentation-only SDD adoption. Compare later changes against this table; do not
"fix" a mismatch by editing the numbers without re-running the commands.

## Environment

| Item | Value |
|---|---|
| Commit | `2b422c9` (branch `sdd`, nearest tag `v0.17.0`) |
| Date | 2026-10-30 |
| OS | Linux (developer machine) |
| .NET SDK | `10.0.403` |
| Docker | `29.9.2` (Testcontainers available) |
| Solution | `src/Sa.slnx` |
| Warnings baseline | 0 build warnings, 0 errors |

## Commands and results

All commands run from the repository root.

### 1. Build — `./build-sh/do-build.sh`

- Exit code: `0`
- Result: `Build succeeded. 0 Warning(s), 0 Error(s)`, elapsed `00:00:18`
- Scope: clean + restore + build of all 48 projects, Release.

### 2. Test — `./build-sh/do-test.sh`

- Exit code: `0`
- Result: `total: 2712`, `succeeded: 2711`, `skipped: 1`, `failed: 0`, duration `4m 56s 908ms`.
- Environment note: `--max-parallel-test-modules 2` and `xunit maxParallelThreads: 2`; integration
  assemblies start `postgres:15` / `chrislusf/seaweedfs:4.50` containers.

| Test assembly | Result | Duration |
|---|---|---|
| `Sa.Outbox.PostgreSqlTests` | passed | 2m 17s |
| `Sa.Partitional.PostgreSqlTests` | passed | 1m 23s |
| `Sa.Data.PostgreSqlTests` | passed | 49s 245ms |
| `Sa.Data.S3Tests` | passed | 49s 871ms |
| `Sa.HybridFileStorage.PostgresTests` | passed | 38s 730ms |
| `Sa.HybridFileStorage.S3Tests` | passed | 34s 980ms |
| `Sa.Configuration.PostgreSqlTests` | passed | 28s 513ms |
| `Sa.ConfigurationTests` | passed | 17s 477ms |
| `Sa.Utils.WorkQueue.Tests` | passed | 12s 855ms |
| `Sa.MediaTests` | passed | 11s 530ms |
| `Sa.ScheduleTests` | passed | 10s 353ms |
| `SaTests` | passed | 9s 525ms |
| `Sa.Media.FFmpegTests` | passed | 9s 213ms |
| `Sa.HybridFileStorageTests` | passed | 6s 954ms |
| `Sa.Data.TempFolderTests` | passed (1 skipped: Windows-only case rule) | 5s 883ms |
| `Sa.HybridFileStorage.FileSystemTests` | passed | 4s 771ms |
| `Sa.Outbox.Tests` | passed | 4s 576ms |

Skipped test: `Sa.Data.TempFolderTests.CleanupTests.Cleanup_PrefixMatch_FollowsThePlatformCaseRule`
(“Only Windows compares folder names case-insensitively by default.”) — platform-conditional, not a
regression.

### 3. Package — `./build-sh/do-package.sh`

- Exit code: `0`
- Result: 16 `.nupkg` + 16 `.snupkg` in `dist/`, one pair per published project.
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

`Sa` (`src/Sa/Sa.csproj`) is intentionally absent: it is a shared-source library, not a package.

### 4. Package check — `./build-sh/do-package-check.sh`

Run by CI on both OS legs; packs `Sa.Media.FFmpeg`, restores it into a throwaway feed and builds a
consumer against the `.nupkg` to prove the bundled FFmpeg payload lands in the consumer output.

- Exit code: `0`
- Observed: consumer output contains `sa/native/ffmpeg`, and the bundled binary reports
  `ffmpeg version 7.1.5`; the script prints `Package payload OK (0.17.0)`.

## Post-change verification

Documentation stage 2 (grouping `docs/`) is docs-only, but packaging reads `docs/NuGet.md`, so
`./build-sh/do-package.sh` was re-run afterwards: exit `0`, 16 `.nupkg`, and
`Sa.Media.FFmpeg.0.17.0.nupkg` still contains `NuGet.md` and `icon.png`. Build and test results are
unchanged from the tables above.

## Reproducing

```bash
git checkout 2b422c9
./build-sh/do-build.sh
./build-sh/do-test.sh        # requires Docker
./build-sh/do-package.sh
./build-sh/do-package-check.sh
```

If the numbers differ, record the new environment (SDK, Docker, RAM, `SA_TEST_PARALLELISM`) before
concluding that a library regressed.
