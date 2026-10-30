# SPEC-004: Move standalone libraries into `Misc/` inside `src/Libraries/`

- Status: Draft
- Owner: TBD
- Related issues/ADRs: ADR-0001, SPEC-000 (baseline), SPEC-001 (`src/Libraries`), SPEC-003 (`libraries-domain-groups`)
- Last updated: 2026-10-30

## Context / problem

SPEC-001 moved all 17 library projects into `src/Libraries/`. SPEC-003 grouped the 13 domain projects into
`Configuration/`, `Data/`, `Hybrid/`, `Media/`, `Outbox/` and left the 4 standalone projects
(`Sa`, `Sa.Partitional.PostgreSql`, `Sa.Schedule`, `Sa.Utils.WorkQueue`) directly in `src/Libraries/`.

`Sa` is not packed and is a shared-source base — it legitimately has no domain group. The three
infrastructure standalone projects — `Sa.Partitional.PostgreSql`, `Sa.Schedule`, `Sa.Utils.WorkQueue` — are
standalone only in the sense that no other library occupies the same folder: they are consumed cross-group by
`Sa.Outbox`, `Sa.Outbox.PostgreSql` and `Sa.HybridFileStorage.Postgres`. Their flat placement under
`src/Libraries/` therefore hides the boundary between "no domain" and "domain group of its own", and forces
every future grouping decision to touch the root of the library tree.

## Goals

- Move `Sa.Partitional.PostgreSql`, `Sa.Schedule`, `Sa.Utils.WorkQueue` into `src/Libraries/Misc/`.
- Keep package identity, public API, TFMs and build behavior unchanged, and update every path-dependent
  reference in the same change.

## Non-goals

- Renaming projects, assemblies, namespaces or NuGet package IDs.
- Changing dependencies, versions or any source code.
- Moving samples, tests, benchmarks or `src/*.props|xml`.
- Creating `Core/` or `Infrastructure/` groups (explicitly declined; `Misc` is the chosen name).

## Target layout

```text
src/Libraries/
├── Sa/                            shared-source base (not packed)
├── Configuration/
│   ├── Sa.Configuration/
│   └── Sa.Configuration.PostgreSql/
├── Data/
│   ├── Sa.Data.PostgreSql/
│   ├── Sa.Data.S3/
│   └── Sa.Data.TempFolder/
├── Hybrid/
│   ├── Sa.HybridFileStorage/
│   ├── Sa.HybridFileStorage.FileSystem/
│   ├── Sa.HybridFileStorage.Postgres/
│   └── Sa.HybridFileStorage.S3/
├── Media/
│   ├── Sa.Media/
│   └── Sa.Media.FFmpeg/
├── Misc/
│   ├── Sa.Partitional.PostgreSql/
│   ├── Sa.Schedule/
│   └── Sa.Utils.WorkQueue/
└── Outbox/
    ├── Sa.Outbox/
    └── Sa.Outbox.PostgreSql/
```

## Actors and scenarios

### Scenario: build after regrouping into `Misc/`

**Given** a clean checkout
**When** `./build-sh/do-build.sh` runs
**Then** all 48 projects build with 0 warnings / 0 errors, as in the SPEC-000 baseline.

### Scenario: package identity unchanged

**Given** the regrouped tree
**When** `./build-sh/do-package.sh` runs
**Then** it produces the same 16 package IDs at the same versions with the same TFMs, dependencies,
`NuGet.md` and `icon.png`.

### Scenario: adding a package to `Misc/`

**Given** the grouped layout
**When** a new infrastructure library is added
**Then** it goes into `src/Libraries/Misc/` and uses `../../../../Common.NuGet.Properties.xml`, `..\..\Sa\...`
for shared-source links and `..\..\<SiblingLib>` for same-group references.

## Functional requirements

- **R-1**: The 3 standalone projects live in `Misc/`; the shared-source base `Sa` stays directly in
  `src/Libraries/`.
- **R-2**: Every path-dependent reference is updated: shared props imports (depth becomes 3 →
  `..\..\..\Common.NuGet.Properties.xml`), cross-group `ProjectReference`s (e.g. Outbox →
  `..\..\Misc\Sa.Schedule\Sa.Schedule.csproj`, Hybrid → `..\..\Misc\Sa.Partitional.PostgreSql\...`), samples,
  tests and benchmarks references (`..\..\Libraries\Misc\...`), `<Compile Include="..\..\Sa\...">` links
  (shared source is now three levels up), `src/Sa.slnx` (new `/Libraries/Misc/` solution folder).
- **R-3**: Packaging stays group-agnostic: `nu_pack()` locates `<Project>.csproj` by name under
  `src/Libraries/` (depth-independent), so no pack-list change is required.
- **R-4**: Package metadata is unchanged (16 IDs/versions/TFMs/dependencies/embedded files).

## Non-functional requirements

- **Compatibility**: `net10.0`, AOT settings and analyzers unchanged; 0 build warnings.
- **Performance**: none at runtime; build/test times unchanged within noise.
- **Reliability**: same test totals as the baseline (2712 / 2711 passed / 1 skipped / 0 failed).
- **Security/privacy**: no secrets touched.
- **Observability**: not applicable.

## Constraints and dependencies

- `src/Directory.*.props`, `src/global.json`, `src/nuget.config`, `src/Common.*.xml` and
  `src/Tests/Host.Test.Properties.xml` stay where they are (MSBuild/NuGet discover them by walking up).
- `src/Common.NuGet.Properties.xml` keeps working via `$(MSBuildThisFileDirectory)` (depth-independent).

## Acceptance criteria

See `acceptance.md`.

## Open questions

- None. Decision: the group is named `Misc`.

## Change log

- 2026-10-30: Initial spec.
- 2026-10-30: Move executed (`git mv` into `src/Libraries/Misc/`); build green (0/0, 48 projects), scripted
  reference resolution `unresolved 0`, markdown link check `broken 0`; affected unit suites green
  (`Sa.ScheduleTests` 353/353, `Sa.Utils.WorkQueue.Tests` 188/188). Full-suite/package regression pending.
