# SPEC-000: Project baseline and SDD adoption

- Status: Approved
- Owner: TBD
- Related issues/ADRs: ADR-0001 (SDD and repository layout)
- Last updated: 2026-10-30

## Context / problem

Sa is a monorepo of independent, publishable .NET 10 libraries for distributed-systems
infrastructure. Until now there was no normative place for requirements: behavior was described
by code, per-library `Readme.md` files and the root `README.md`, so "what must not break" was
implicit. Any folder reorganization or cross-cutting change therefore carried an undocumented
regression risk.

This spec establishes the repository-level invariants and the spec-first change process, and it
records the baseline facts measured on the checkout it was written against. It changes no runtime
behavior and moves no production project.

**Baseline anchor:** commit `2b422c9` (branch `sdd`, nearest tag `v0.17.0`), measured 2026-10-30.

## Goals

- Make requirements, invariants and acceptance criteria discoverable and reviewable.
- Record the current package/project boundaries as verified facts, not assumptions.
- Preserve build, test, package and public API behavior while SDD is adopted.
- Enable later folder restructuring only through separately reviewed migrations with a rollback plan.

## Non-goals

- Changing library behavior, public API, package IDs or namespaces.
- Changing target frameworks or dropping Native AOT compatibility.
- Moving production projects (`src/<PackageName>`) in this change.
- Replacing the existing build scripts (`build-sh/`, `build/`) or the CI provider.
- Enforcing spec format in CI (a later, separate task).

## Actors and scenarios

Actors: library maintainer, contributor opening a PR, release owner, CI.

### Scenario: new behavior-changing feature

**Given** a proposed feature that changes observable behavior or public API
**When** the contributor starts work
**Then** a `specs/NNN-<slug>/spec.md` with testable acceptance criteria exists before the
implementation plan, and the PR references its spec ID.

### Scenario: structural migration

**Given** a proposed move/rename of projects or folders
**When** the change is prepared
**Then** it has its own spec/ADR, an exact move list, an audit of every path-dependent reference
(`.slnx`, `.csproj`, props/targets, scripts, workflows, docs) and a rollback plan, and it does not
mix in functional changes.

### Scenario: baseline verification

**Given** a clean checkout of the baseline commit
**When** the canonical build, test and package commands run
**Then** the results match the recorded baseline in `baseline.md`, and package IDs, TFMs and
public API are unchanged.

## Functional requirements

- **R-1**: The repository root exposes the SDD entry point: root `README.md` links to
  `docs/README.md` (process), `specs/` (requirements) and `checklists/`.
- **R-2**: Every behavior-changing feature has a spec with testable acceptance criteria before
  implementation.
- **R-3**: Every structural migration lists affected paths and updates all path-dependent
  references in the same change.
- **R-4**: SDD documentation adoption does not alter compiled assemblies or NuGet package metadata.
- **R-5**: The canonical commands remain usable unchanged: `./build-sh/do-build.sh`,
  `./build-sh/do-test.sh`, `./build-sh/do-package.sh`, and the documented direct `dotnet` commands.
- **R-6**: Public API, package IDs, target frameworks and Native AOT compatibility are explicit
  invariants unless a spec says otherwise.
- **R-7**: Canonical layout: normative requirements in `specs/`, work templates in `templates/`,
  review gates in `checklists/`, ADRs in `docs/decisions/`, explanatory docs in `docs/`. Build
  inputs never reference `specs/`, `templates/` or `checklists/`.
- **R-8**: `specs/000-project-baseline/baseline.md` records the measured baseline (commit, toolchain,
  command results) and is reproducible on a clean checkout.

## Non-functional requirements

- **Compatibility**: all projects target `net10.0`; libraries keep `PublishAot=true` and
  `IsAotCompatible=true` with analyzers enabled. TFM changes require a new spec.
- **Performance**: no performance requirement in this spec; SDD artifacts are not part of any build.
- **Reliability**: integration suites need Docker (PostgreSQL + SeaweedFS via Testcontainers).
  Concurrency is capped deliberately: `maxParallelThreads: 2` per assembly (`src/Tests/xunit.runner.json`)
  and `--max-parallel-test-modules 2` across assemblies (`build-sh/tasks.sh`, override with
  `SA_TEST_PARALLELISM`). Raising either on a normal dev box causes resource failures that look
  like product bugs.
- **Security/privacy**: no secrets in the repository; `.secrets/` is untracked and Docker/registry
  credentials come from the environment (`build-sh/do-secrets.sh`).
- **Observability**: not applicable to this spec.

## Constraints and dependencies

Verified on the baseline commit (48 `.csproj`, 1 solution):

- Solution: `src/Sa.slnx` (XML solution format), organized in solution folders.
- Projects: 17 libraries (`src/Sa` + 16 `src/Sa.*`), 9 samples (`src/Samples`), 2 benchmarks
  (`src/Benchmarks`), 17 test projects (`src/Tests`), 3 fixture projects (`src/Tests/Fixtures`).
- Build props: every library imports `src/Common.NuGet.Properties.xml` → `src/Common.Properties.xml`
  (TFM, analyzers, SourceLink, `GeneratePackageOnBuild`, packed `docs/NuGet.md` + `icon.png`).
  `src/Directory.Build.props` only sets a non-Windows restore fallback folder.
- Central package management: `src/Directory.Packages.props`, 21 pinned versions,
  `ManagePackageVersionsCentrally=true`; `.csproj` files carry no `<Version/>` on `PackageReference`.
- Published packages: 16 (`build-sh/tasks.sh` `PROJECTS`). Package ID = assembly/project name; no
  explicit `<PackageId>` exists. Current versions: `0.17.0` for all except
  `Sa.Partitional.PostgreSql` = `0.18.0`. `src/Sa` is not packed: it is a shared-source library
  whose files are `<Compile Include>`-linked into consuming projects (and nothing references
  `Sa.csproj` as a `ProjectReference`).
- Tests: xUnit v3 on Microsoft.Testing.Platform; test projects are executables
  (`src/Tests/Host.Test.Properties.xml`), `src/global.json` selects the MTP runner, so `dotnet test`
  must be invoked from inside `src/`. Fixture projects set `IsTestingPlatformApplication=false` so
  they are not discovered as test modules (avoids MTP exit code 8).
- Test infrastructure: Testcontainers with `postgres:15`, `chrislusf/seaweedfs:4.50` and
  `testcontainers/ryuk:0.14.0`; one container per test class.
- NuGet: `src/nuget.config` uses `nuget.org` + local feed `./nupkgs`, with the global packages
  folder redirected to `src/.packages`.
- CI: `.github/workflows/dotnet.yml` runs on `ubuntu-latest` + `windows-latest`; build via
  `./build-sh/do-build.sh`, full test suite on Linux (`./build-sh/do-test.sh`), FFmpeg tests only on
  Windows, then `./build-sh/do-package-check.sh`.
- Toolchain on the baseline machine: .NET SDK `10.0.403`, Docker `29.9.2`.

## Acceptance criteria

See `acceptance.md` (AC-1 … AC-8).

## Open questions

- None blocking. Decisions taken while filling this spec:
  - **ADR location**: `docs/decisions/` (not `specs/decisions/`). SDD dirs live at the repository
    root (`specs/`, `templates/`, `checklists/`), as in ADR-0001 and `docs/architecture/target-architecture.md`.
  - **`src/Libraries/` grouping**: deferred. Current flat `src/<PackageName>` layout stays until a
    dedicated migration spec shows a concrete benefit outweighing the path churn.
  - **CI spec-format validation**: deferred to a separate spec (see stage 4 of the migration plan).
  - **`src/Sa` packaging**: intentionally unpacked shared-source library; not a defect. Any change
    to that model needs its own spec.

## Change log

- 2026-10-30: Initial draft based on the public README.
- 2026-10-30: Filled with facts verified on `2b422c9`; canonical SDD paths pinned to the repository
  root; open questions resolved (see above).
- 2026-10-30: Approved after reviewing the recorded baseline. Documentation stage 2 completed:
  `docs/` grouped into `architecture/`, `development/`, `decisions/`, `repository/`; no production
  path changed and `dotnet pack` still embeds `NuGet.md` + `icon.png`.
- 2026-10-30: Library layout superseded by SPEC-001 (`specs/001-src-libraries-layout/`): the 17
  library projects moved to `src/Libraries/`. Package IDs, versions, TFMs and API are unchanged;
  baseline numbers still hold.
