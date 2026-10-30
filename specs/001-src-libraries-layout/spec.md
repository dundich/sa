# SPEC-001: Group library projects under `src/Libraries/`

- Status: Implemented
- Owner: TBD
- Related issues/ADRs: ADR-0001 (SDD and repository layout), SPEC-000 (baseline invariants)
- Last updated: 2026-10-30

## Context / problem

`src/` currently mixes three different kinds of project at the same level: 17 library projects
(`src/Sa`, `src/Sa.*`), `src/Samples`, `src/Tests` and `src/Benchmarks`. Libraries are the only
publishable artifact of the repository, but nothing in the layout says so: a reader of `src/` sees 17
package folders next to `Samples`/`Tests`/`Benchmarks`.

SPEC-000 R-7 and ADR-0001 deferred this grouping until the baseline inventory existed. The inventory
(`docs/repository/project-structure.md`) now lists every path-dependent reference, so the move can be
made with a complete audit.

## Goals

- Group all 17 library projects under `src/Libraries/<PackageName>/`, leaving samples, tests,
  benchmarks and build tooling in place.
- Keep package identity, public API, target frameworks, dependencies and build behavior byte-identical.
- Update every path-dependent reference in the same change, so nothing depends on the old paths.

## Non-goals

- Renaming projects, assemblies, namespaces or NuGet package IDs.
- Changing versions, dependencies, TFMs, AOT settings or any source code semantics.
- Moving `src/Samples`, `src/Tests`, `src/Benchmarks`, `build-sh/`, `build/`, `.github/` or `docs/`.
- Turning `src/Libraries/Sa` into a real project reference (it stays a linked shared-source library).
- Reformatting or otherwise editing files that do not need a path change.

## Actors and scenarios

### Scenario: maintainer builds the repository after the move

**Given** a clean checkout of the change
**When** `./build-sh/do-build.sh` runs
**Then** all 48 projects restore/build with 0 warnings and 0 errors, as in the SPEC-000 baseline.

### Scenario: package identity is preserved

**Given** the same source tree before and after the move
**When** `./build-sh/do-package.sh` runs
**Then** it still produces the same 16 package IDs at the same versions, with the same TFMs,
dependencies, `NuGet.md` and `icon.png` inside each package.

### Scenario: contributor adds a package

**Given** the new layout
**When** a new library is added under `src/Libraries/<Name>/`
**Then** it follows the same relative paths as its siblings: `../../Common.NuGet.Properties.xml`,
`../<OtherLib>/<OtherLib>.csproj`, `../Sa/...` for shared source, and it is added to `src/Sa.slnx`
under the `/Libraries/` solution folder.

### Scenario: rollback

**Given** a regression caused by the move
**When** the change is reverted
**Then** one revert restores the previous layout completely, because no functional change is mixed in.

## Functional requirements

- **R-1**: The 17 library projects live under `src/Libraries/<ProjectName>/`; directory and file names
  are unchanged, so package IDs, assembly names and namespaces are unchanged.
- **R-2**: All path-dependent references are updated in the same change:
  `src/Sa.slnx`, `src/Common.NuGet.Properties.xml` (packed `docs/NuGet.md`, `icon.png`),
  every `Import` of the shared props (`../../Common.*.xml`), every `ProjectReference` from samples and
  tests, every `<Compile Include="..\Sa\...">` link, every `<ProjectReference>` between libraries,
  `build-sh/tasks.sh`, `build/tasks.ps1`, `build-sh/package-check.sh`, `.github/workflows/dotnet.yml`
  (line-ending normalization path), `.gitignore` (FFmpeg payload/artifact patterns), and all
  documentation links.
- **R-3**: `./build-sh/do-build.sh`, `./build-sh/do-test.sh`, `./build-sh/do-package.sh` and
  `./build-sh/do-package-check.sh` succeed with results matching `specs/000-project-baseline/baseline.md`.
- **R-4**: Package metadata is unchanged: 16 IDs, same versions (`Sa.Partitional.PostgreSql` 0.18.0,
  the rest 0.17.0), `net10.0`, same dependency sets, `NuGet.md` + `icon.png` embedded.
- **R-5**: The solution builds with the same 48 projects; solution folders show libraries under
  `/Libraries/`.
- **R-6**: Documentation reflects the new paths and contains no link to a removed location
  (`README.md`, `README-ru.md`, `AGENTS.md`, `docs/**`, `specs/**`).
- **R-7**: The change contains no functional, formatting or version edit; it can be reverted as one unit.

## Non-functional requirements

- **Compatibility**: `net10.0`, `PublishAot=true`, `IsAotCompatible=true` unchanged; 0 build warnings.
- **Performance**: no runtime impact expected; move must not change published binary contents beyond
  embedded source paths (SourceLink/`EmbedUntrackedSources`), which are not part of package identity.
- **Reliability**: same test totals as the baseline (2712 total, 2711 passed, 1 platform skip).
- **Security/privacy**: no secrets or credentials touched.
- **Observability**: not applicable.

## Constraints and dependencies

- `src/Directory.Build.props`, `src/Directory.Packages.props`, `src/global.json`, `src/nuget.config`
  and `src/Tests/Host.Test.Properties.xml` stay where they are: MSBuild and NuGet discover them by
  directory walk from each project, and the move only adds one directory level below `src/`.
- Shared source (`src/Libraries/Sa/Classes/*.cs`, `src/Libraries/Sa/Extensions/*.cs`) moves together with its consumers,
  so every relative `..\Sa\...` link keeps working unchanged.
- `dotnet test` must still be invoked from inside `src/` (MTP runner selection in `src/global.json`).
- The Docker images and Testcontainers concurrency caps are unchanged.

## Acceptance criteria

See `acceptance.md`.

## Open questions

- None blocking. Decisions taken:
  - All 17 projects move, including the non-packable `src/Libraries/Sa` and the published `Sa.Data.PostgreSql`
    and `Sa.Outbox` that older diagrams omitted. Rationale: one rule for every library.
  - Benchmarks and samples stay in `src/`, because they consume libraries rather than being libraries.
  - No `Directory.Build.props` change is needed; the existing one is intentionally minimal.

## Change log

- 2026-10-30: Initial spec, derived from `docs/repository/project-structure.md`.
- 2026-10-30: Implemented. Build 0 warnings/0 errors, tests 2712/2711/1/0 (identical to baseline),
  16 packages with unchanged IDs/versions, package check green. Documentation, `AGENTS.md` and the
  inventory updated; `src/Libraries/Sa.Schedule/Readme*.md` links fixed for the new depth.
- 2026-10-30: Follow-up found during the SPEC-002 audit — `.gitignore` still used
  `/src/Sa.Media.FFmpeg/...` patterns, so the FFmpeg payload dir (`sa/native/`) and build artifacts
  became untracked-but-not-ignored after the move. Patterns updated to `src/Libraries/…`.
