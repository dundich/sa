# SPEC-002: Fix build-script defects surfaced by the library move audit

- Status: Implemented
- Owner: TBD
- Related issues/ADRs: ADR-0001, SPEC-000 (baseline), SPEC-001 (path move)
- Last updated: 2026-10-30

## Context / problem

While auditing every `build-sh/` script after SPEC-001, five defects were found. They are
pre-existing (not caused by the move), but they make the tooling silently weaker than its
documentation:

1. `clean()` never cleans. `build-sh/tasks.sh` routes the “Clean” step through `_msbuild`, which runs
   `dotnet build`, so `./build-sh/do-build.sh` — advertised as “clean + restore + build” — deletes only
   `dist/` and leaves `bin/`/`obj/`. Incremental builds can therefore hide stale artifacts.
2. `do-push-local.sh` pushes to a source named `local`, which exists neither in the repository config
   (`src/nuget.config` calls it `local.nupkgs`, and it is scoped under `src/`) nor by default on a
   developer machine. The script fails with `The specified source 'local' is invalid`.
3. `do-push-prod.sh` calls `dotnet nuget push` against nuget.org without `--api-key`; with no
   `<apikeys>` entry it fails with 401 and gives no hint about the required credential.
4. `nu_push` exits successfully when `dist/` is empty: `find` yields nothing, the loop body never
   runs, and the script reports success without pushing anything.
5. `do-fix-max-path-linux.sh` takes its symlink target from `$(pwd)` instead of the repository root, so
   running it from `build-sh/` (where it lives) creates `/sa -> build-sh`.

## Goals

- Make each script behave as documented and fail loudly instead of silently doing nothing.
- Keep the change inside `build-sh/` (plus its readme); do not alter build outputs, package identity
  or CI semantics.
- Keep `do-push-prod-ex.sh` untouched.

## Non-goals

- Rewriting the scripts, changing their CLI, or changing what gets packed/tested.
- Fixing the PowerShell twins in `build/` (they have a different, smaller surface; separate change).
- Changing NuGet sources of the repository (`src/nuget.config`).
- Adding shellcheck/CI linting.

## Actors and scenarios

### Scenario: clean build really is clean

**Given** a previous build left `bin/`/`obj/` under `src/**`
**When** `./build-sh/do-build.sh` runs
**Then** `dotnet clean` runs before `dotnet build`, so the build does not reuse previous outputs.

### Scenario: local push works out of the box

**Given** packages in `dist/` and a checkout of this repository
**When** `./build-sh/do-push-local.sh` runs
**Then** packages are pushed into the repository-local feed `src/nupkgs` (the same folder that
`src/nuget.config` maps for `Sa.*`), without requiring any pre-registered source.

### Scenario: prod push fails with guidance, not a 401

**Given** no `NUGET_API_KEY` in the environment
**When** `./build-sh/do-push-prod.sh` runs
**Then** it exits non-zero immediately with a message naming the variable, and no package is pushed.
With the variable set, the key is passed via `--api-key` and nothing is echoed.

### Scenario: empty dist is an error

**Given** an empty `dist/`
**When** any push script runs
**Then** it exits non-zero with “no packages — run do-package.sh” and does not report success.

### Scenario: long-path helper targets the repository

**Given** any working directory
**When** `sudo ./build-sh/do-fix-max-path-linux.sh` runs
**Then** `/sa` points at the repository root (the parent of the script directory).

## Functional requirements

- **R-1**: `clean()` runs `dotnet clean` for the solution before `dotnet build`; `dist/` is still wiped.
- **R-2**: `do-push-local.sh` pushes to `$ROOT/src/nupkgs`, creating the directory if needed; no
  user-level NuGet source is required.
- **R-3**: `do-push-prod.sh` requires `NUGET_API_KEY`, exits non-zero with a clear message when it is
  missing, and passes it as `--api-key` when present. The key is never printed.
- **R-4**: `nu_push` fails with a clear error when `dist/` contains no `.nupkg`/`.snupkg`.
- **R-5**: `do-fix-max-path-linux.sh` resolves the repository root from its own location.
- **R-6**: `build-sh/readme.md` documents the new behavior (real clean, local feed path, API-key
  requirement).
- **R-7**: `do-push-prod-ex.sh` is not modified.

## Non-functional requirements

- **Compatibility**: bash 4+ with `set -euo pipefail`, same as the rest of `build-sh/`; no new
  dependencies.
- **Performance**: clean builds are slower by design; nothing else changes.
- **Reliability**: failure paths exit non-zero and print an actionable message.
- **Security/privacy**: the API key is taken from the environment only and never echoed.
- **Observability**: unchanged `_step` output style.

## Constraints and dependencies

- `src/nupkgs` is a tracked directory (`.gitkeep` only); `*.nupkg`/`*.snupkg` are git-ignored.
- `src/nuget.config` maps `Sa.*` to `./nupkgs` and sets the global packages folder to `src/.packages`;
  neither is changed.
- `do-push-prod-ex.sh` keeps working as before.

## Acceptance criteria

See `acceptance.md`.

## Open questions

- None blocking. Decisions:
  - Local feed path is hard-coded as `src/nupkgs` rather than introducing a configurable variable;
    it is the path the repository config already uses.
  - The prod key is read from `NUGET_API_KEY` (the community convention) instead of a new name.

## Change log

- 2026-10-30: Initial spec from the `build-sh/` audit performed after SPEC-001.
- 2026-10-30: Implemented. `clean()` now runs `dotnet clean`; local push targets `src/nupkgs`; prod push
  requires `NUGET_API_KEY`; `nu_push` fails on an empty `dist/`; long-path helper derives the repo root
  from the script location. Verified: build 0/0, tests 2712/2711/1/0, package 16+16, package check
  green, secrets check green, all scripts pass `bash -n`.
