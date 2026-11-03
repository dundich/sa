# SPEC-003: Group libraries by domain inside `src/Libraries/`

- Status: Implemented
- Owner: TBD
- Related issues/ADRs: ADR-0001, SPEC-000 (baseline), SPEC-001 (`src/Libraries`), SPEC-002 (tooling)
- Last updated: 2026-10-30

## Context / problem

After SPEC-001 all 17 library projects sit flat in `src/Libraries/`. The flat list still hides the
domain boundaries that already exist in the code and in the solution: configuration providers, data
adapters, the outbox family, the hybrid file storage family and media. A reader of `src/Libraries`
cannot tell which projects form a family, and solution folders do not express it either.

## Goals

- Group the 13 projects that belong to a domain into `Configuration/`, `Data/`, `Outbox/`, `Hybrid/`
  and `Media/`.
- Keep the 4 standalone projects (`Sa`, `Sa.Partitional.PostgreSql`, `Sa.Schedule`,
  `Sa.Utils.WorkQueue`) directly in `src/Libraries/`.
- Keep package identity, public API, TFMs and build behavior unchanged, and update every
  path-dependent reference in the same change.

## Non-goals

- Renaming projects, assemblies, namespaces or NuGet package IDs.
- Changing dependencies, versions or any source code.
- Moving samples, tests, benchmarks or `src/*.props|xml`.
- Splitting `src/Sa` shared source into its own package.
- Creating `Core/` or `Infrastructure/` groups (explicitly declined).

## Target layout

```text
src/Libraries/
├── Sa/                            shared-source base (not packed)
├── Sa.Partitional.PostgreSql/
├── Sa.Schedule/
├── Sa.Utils.WorkQueue/
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
└── Outbox/
    ├── Sa.Outbox/
    └── Sa.Outbox.PostgreSql/
```

## Actors and scenarios

### Scenario: build after regrouping

**Given** a clean checkout
**When** `./build-sh/do-build.sh` runs
**Then** all 48 projects build with 0 warnings / 0 errors, as in the SPEC-000 baseline.

### Scenario: package identity unchanged

**Given** the regrouped tree
**When** `./build-sh/do-package.sh` runs
**Then** it produces the same 16 package IDs at the same versions with the same TFMs, dependencies,
`NuGet.md` and `icon.png`.

### Scenario: adding a package to a group

**Given** the grouped layout
**When** a new library is added
**Then** it goes into its domain folder (or `src/Libraries/` when it has no domain) and uses
`../../../Common.NuGet.Properties.xml`, `..\<OtherGroup>\<OtherLib>\<OtherLib>.csproj` or
`..\..\<Lib>\<Lib>.csproj` for cross-group references.

### Scenario: tooling finds a project without a hard-coded group

**Given** the pack list in `build-sh/tasks.sh`
**When** `do-package.sh` runs
**Then** each project is located by name under `src/Libraries/` (depth-independent), so a later
regrouping does not break packaging.

## Functional requirements

- **R-1**: The 13 domain projects live in the folders from *Target layout*; the other 4 stay directly
  under `src/Libraries/`. Project directory and file names are unchanged.
- **R-2**: Every path-dependent reference is updated: shared props imports (`../../..`), cross-project
  `ProjectReference`s (same group → `..\<Lib>`, other group → `..\<Group>\<Lib>`, root → `..\..\<Lib>`),
  `<Compile Include="..\Sa\...">` links (one level deeper), `src/Sa.slnx` (paths + nested solution
  folders), samples/tests/benchmarks/fixtures references, pack paths in `build-sh/tasks.sh` and
  `build/tasks.ps1`, `build-sh/package-check.sh`, `.github/workflows/dotnet.yml`, `.gitignore` and all
  documentation links.
- **R-3**: `do-build.sh`, `do-test.sh`, `do-package.sh`, `do-package-check.sh` match the baseline.
- **R-4**: Package metadata is unchanged (16 IDs/versions/TFMs/dependencies/embedded files).
- **R-5**: The solution lists the same 48 projects with `/Libraries/<Group>/` solution folders.
- **R-6**: Documentation reflects the grouped layout and contains no link to a removed path.
- **R-7**: No functional, formatting or version change is mixed in; one revert undoes the move.

## Non-functional requirements

- **Compatibility**: `net10.0`, AOT settings and analyzers unchanged; 0 build warnings.
- **Performance**: none at runtime; build/test times unchanged within noise.
- **Reliability**: same test totals as the baseline (2712 / 2711 passed / 1 skipped / 0 failed).
- **Security/privacy**: no secrets touched.
- **Observability**: not applicable.

## Constraints and dependencies

- `src/Directory.*.props`, `src/global.json`, `src/nuget.config`, `src/Common.*.xml` and
  `src/Tests/Host.Test.Properties.xml` stay where they are (MSBuild/NuGet discover them by walking up).
- `src/Common.NuGet.Properties.xml` keeps working: its packed paths are relative to `src/`, not to the
  project, so they do not change.
- `dotnet test` still runs from inside `src/`.

## Acceptance criteria

See `acceptance.md`.

## Open questions

- None. Decisions: the 5 groups are named exactly `Configuration`, `Data`, `Outbox`, `Hybrid`,
  `Media`; `Hybrid` is the `Sa.HybridFileStorage` family; standalone projects stay at the root.

## Change log

- 2026-10-30: Initial spec.
- 2026-10-30: Implemented. 13 projects moved into `Configuration/`, `Data/`, `Hybrid/`, `Media/`,
  `Outbox/`; standalone projects stayed at the root. All relative references rewritten, solution
  folders added, packaging made group-agnostic, docs updated. Verified: build 0/0 (48 projects),
  tests 2712/2711/1/0, 16+16 packages at unchanged versions, package check green, 0 unresolved
  references, 0 broken Markdown links.
- 2026-10-30: Note — `src/Common.NuGet.Properties.xml` now resolves the packed `docs/NuGet.md` and
  `icon.png` via `$(MSBuildThisFileDirectory)`; the old `..\..\..\` form resolved by project depth and
  broke for the one-level-deeper grouped projects.
- 2026-10-30: Note — nested `<Folder>` elements inside `<Folder>` in `Sa.slnx` are ignored by the SDK
  (the 13 grouped projects disappeared from `dotnet sln list`); the solution uses flat
  `/Libraries/<Group>/` folder declarations instead.
