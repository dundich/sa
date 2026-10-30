# Repository inventory (baseline)

Verified on commit `2b422c9` (branch `sdd`), 2026-10-30, from the working tree — not from memory or
the public README. Regenerate the file lists with the commands at the end of this page before
relying on them in a migration spec.

## Top-level layout

```text
/
├── .github/workflows/dotnet.yml     CI: build + tests + package check
├── build-sh/                        POSIX bash helpers (source of truth for CI)
├── build/                           PowerShell counterparts (do_*.ps1, tasks.ps1)
├── checklists/                      DoR / DoD / repository-move gates
├── docs/                            explanatory docs, ADRs, NuGet readme payload
│   ├── architecture/                target structure
│   ├── development/                 SDD workflow + migration plan
│   ├── decisions/                   ADR-0001 …
│   ├── repository/                  assessment + this inventory
│   └── NuGet.md                     packed into every package as PackageReadmeFile
├── specs/                           normative specs and acceptance criteria
├── templates/                       spec / plan / tasks templates
├── src/
│   ├── Sa.slnx                      solution (XML .slnx)
│   ├── Directory.Build.props        non-Windows NuGet fallback folder only
│   ├── Directory.Packages.props     central package versions (21 entries)
│   ├── global.json                  MTP test runner selection
│   ├── nuget.config                 sources + packageSourceMapping + local .packages cache
│   ├── Common.Properties.xml        shared library properties
│   ├── Common.NuGet.Properties.xml  packaging properties (imports the above)
│   ├── Libraries/                   all 17 library projects, grouped by domain
│   │   ├── Sa/                      unpacked shared-source library
│   │   └── Misc/                    Sa.Partitional.PostgreSql, Sa.Schedule, Sa.Utils.WorkQueue
│   │       ├── Sa.Partitional.PostgreSql/
│   │       ├── Sa.Schedule/
│   │       └── Sa.Utils.WorkQueue/
│   │   ├── Configuration/           Sa.Configuration, Sa.Configuration.PostgreSql
│   │   ├── Data/                    Sa.Data.PostgreSql, Sa.Data.S3, Sa.Data.TempFolder
│   │   ├── Hybrid/                  Sa.HybridFileStorage + FileSystem/Postgres/S3
│   │   ├── Media/                   Sa.Media, Sa.Media.FFmpeg
│   │   └── Outbox/                  Sa.Outbox, Sa.Outbox.PostgreSql
│   ├── Samples/                     9 console/web samples
│   ├── Benchmarks/                  2 benchmark projects (not packed)
│   └── Tests/                       17 test projects + 3 fixture projects
├── icon.png                         packed as PackageIcon
├── README.md / README-ru.md
└── AGENTS.md                        build/test/packaging guidance for agents
```

Counts: 48 `.csproj`, 1 solution, 1199 `.cs` files (including samples/tests/benchmarks).

## Publishable libraries (16 packages)

All live in `src/Libraries/[<Group>/]<ProjectName>/`, where `<Group>` is one of `Configuration`,
`Data`, `Hybrid`, `Media`, `Misc`, `Outbox`; the shared-source project (`Sa`) has no group, and the
infrastructure standalone projects (`Sa.Partitional.PostgreSql`, `Sa.Schedule`, `Sa.Utils.WorkQueue`) live in
`Misc/`. Package ID = assembly name = project name. There is no
explicit `<PackageId>` anywhere.
All target `net10.0`, `PublishAot=true`, `IsAotCompatible=true`, `Nullable=enable`,
analyzers on, SourceLink on, `GeneratePackageOnBuild=true`.

| Project | Version | Project references | Linked shared source |
|---|---|---|---|
| `Sa.Configuration` | 0.17.0 | — | `Sa/Classes/ConfigurationKey.cs` |
| `Sa.Configuration.PostgreSql` | 0.17.0 | `Sa.Data.PostgreSql` | `Sa/Classes/ConfigurationKey.cs` |
| `Sa.Data.PostgreSql` | 0.17.0 | — | `Retry.cs`, `ExceptionExtensions.cs` |
| `Sa.Data.S3` | 0.17.0 | — | — |
| `Sa.Data.TempFolder` | 0.17.0 | — | `Retry.cs`, `ExceptionExtensions.cs` |
| `Sa.HybridFileStorage` | 0.17.0 | — | — |
| `Sa.HybridFileStorage.FileSystem` | 0.17.0 | `Sa.HybridFileStorage`, `Sa.Data.TempFolder` | — |
| `Sa.HybridFileStorage.Postgres` | 0.17.0 | `Sa.HybridFileStorage`, `Sa.Partitional.PostgreSql` | — |
| `Sa.HybridFileStorage.S3` | 0.17.0 | `Sa.Data.S3`, `Sa.HybridFileStorage` | `Sa/Classes/MimeTypeMap.cs` |
| `Sa.Media` | 0.17.0 | — | — |
| `Sa.Media.FFmpeg` | 0.17.0 | — | — (bundles FFmpeg payload via `build/*.sh`) |
| `Sa.Outbox` | 0.17.0 | `Sa.Schedule` | 8 files from `Sa/Classes`, `Sa/Extensions` |
| `Sa.Outbox.PostgreSql` | 0.17.0 | `Sa.Outbox`, `Sa.Partitional.PostgreSql` | 10 files from `Sa/Classes`, `Sa/Extensions` |
| `Sa.Partitional.PostgreSql` | **0.18.0** | `Sa.Schedule`, `Sa.Data.PostgreSql` | 7 files from `Sa/Classes`, `Sa/Extensions` |
| `Sa.Schedule` | 0.17.0 | `Sa.Utils.WorkQueue` | `Sa/Extensions/ExceptionExtensions.cs` |
| `Sa.Utils.WorkQueue` | 0.17.0 | — | — |

Dependency direction: `Sa.Utils.WorkQueue` → `Sa.Schedule` → `Sa.Outbox` → `Sa.Outbox.PostgreSql`;
`Sa.Partitional.PostgreSql` → `Sa.Schedule` + `Sa.Data.PostgreSql`;
`Sa.HybridFileStorage.S3` → `Sa.Data.S3` + `Sa.HybridFileStorage`.

`src/Libraries/Sa` is **not** published and **not** referenced as a `ProjectReference` by anything: it is a
shared-source library linked into the packages above via `<Compile Include="..\Sa\..." Link="..." />`.
Adding a new shared type therefore requires editing each consuming `.csproj`, not just `src/Libraries/Sa`.

## Shared source (linked, not referenced)

`src/Libraries/Sa/Classes/*.cs` and `src/Libraries/Sa/Extensions/*.cs` are compiled into several packages at once. A change
there is a multi-package change. `src/Libraries/Sa/Sa.csproj` removes `Classes/ConfigurationKey.cs` from its own
glob because that file needs `Microsoft.Extensions.Configuration`, which the base project does not
depend on.

## Test surface

17 test projects + 3 fixture projects (`src/Tests/Fixtures`). Test projects are executables on
xUnit v3 / Microsoft.Testing.Platform and import `src/Tests/Host.Test.Properties.xml`. Fixtures set
`IsTestingPlatformApplication=false` so MTP does not discover them as empty test modules.

| Test project | System under test | Needs Docker |
|---|---|---|
| `SaTests` | `Sa` | no |
| `Sa.ConfigurationTests` | `Sa.Configuration` | no |
| `Sa.Configuration.PostgreSqlTests` | `Sa.Configuration.PostgreSql` | PostgreSQL |
| `Sa.Data.PostgreSqlTests` | `Sa.Data.PostgreSql` | PostgreSQL |
| `Sa.Data.S3Tests` | `Sa.Data.S3` | SeaweedFS |
| `Sa.Data.TempFolderTests` | `Sa.Data.TempFolder` | no |
| `Sa.HybridFileStorageTests` | `Sa.HybridFileStorage` | no |
| `Sa.HybridFileStorage.FileSystemTests` | `Sa.HybridFileStorage.FileSystem` | no |
| `Sa.HybridFileStorage.PostgresTests` | `Sa.HybridFileStorage.Postgres` | PostgreSQL |
| `Sa.HybridFileStorage.S3Tests` | `Sa.HybridFileStorage.S3` | SeaweedFS |
| `Sa.MediaTests` | `Sa.Media` | no |
| `Sa.Media.FFmpegTests` | `Sa.Media.FFmpeg` | no (runs the only CI leg on Windows) |
| `Sa.Outbox.Tests` | `Sa.Outbox` | no |
| `Sa.Outbox.PostgreSqlTests` | `Sa.Outbox.PostgreSql` | PostgreSQL |
| `Sa.Partitional.PostgreSqlTests` | `Sa.Partitional.PostgreSql` | PostgreSQL |
| `Sa.ScheduleTests` | `Sa.Schedule` | no |
| `Sa.Utils.WorkQueue.Tests` | `Sa.Utils.WorkQueue` | no |

Integration images: `postgres:15`, `chrislusf/seaweedfs:4.50`, `testcontainers/ryuk:0.14.0`.
Concurrency caps: `maxParallelThreads: 2` (`src/Tests/xunit.runner.json`) and
`--max-parallel-test-modules 2` (`build-sh/tasks.sh`, `SA_TEST_PARALLELISM` override).

## Samples and benchmarks

`src/Samples`: `Configuration.Web`, `Echo`, `FFMpeg.Console`, `HybridFileStorage.Console`,
`Partitional.ConsoleApp`, `PgOutbox.ConsoleApp`, `Schedule.Configuration.Console`,
`Schedule.Console`, `WorkQueue.Console` (+ `docker-compose.yml`).

`src/Benchmarks`: `Sa.Outbox.Benchmarks`, `Sa.Utils.WorkQueue.Benchmarks` — `IsPackable=false`,
`IsTestingPlatformApplication=false`.

## Path-dependent configuration

A structural move must update every item below:

| Concern | Location |
|---|---|
| Solution entries and solution folders | `src/Sa.slnx` |
| Shared library properties | `src/Common.Properties.xml` |
| Packaging properties, packed readme/icon paths (`..\..\..\docs\NuGet.md`, `..\..\..\icon.png`) | `src/Common.NuGet.Properties.xml` |
| Central package versions | `src/Directory.Packages.props` |
| Test host properties, xunit config copy | `src/Tests/Host.Test.Properties.xml`, `src/Tests/xunit.runner.json` |
| MTP runner selection (must run `dotnet test` from `src/`) | `src/global.json` |
| Feed mapping and global packages folder (`src/.packages`) | `src/nuget.config` |
| Pack order / package check / push lists | `build-sh/tasks.sh` (`PROJECTS`, `DOCKER_TEST_IMAGES`), `build/tasks.ps1` |
| FFmpeg payload script path normalization | `.github/workflows/dotnet.yml` (`src/Libraries/Media/Sa.Media.FFmpeg/build/*.sh`) |
| Ignored FFmpeg payload/artifacts | `.gitignore` (`src/Libraries/Media/Sa.Media.FFmpeg/{build,sa/native}`, `src/Libraries/Media/Sa.Media/Echo`) |
| Consumer-from-nupkg check | `build-sh/package-check.sh`, `build-sh/do-package-check.sh` |
| Relative project references and `<Compile Include="..\..\Sa\...">` links | every `src/Libraries/**/*.csproj` |
| Shared props import depth (`../../../Common.*.xml` for grouped projects) | every `src/Libraries/**/*.csproj` |
| Sample `docker-compose.yml` and per-sample paths | `src/Samples/*` |
| Documentation links | `README.md`, `README-ru.md`, `AGENTS.md`, `docs/**`, `specs/**` |

## Regeneration commands

```bash
find src -name '*.csproj' | sort
grep -h -E '<(Version|ProjectReference|Compile Include|InternalsVisibleTo|PackageReference)' src/Libraries/**/*.csproj
git rev-parse --short HEAD && git describe --tags --always
```
