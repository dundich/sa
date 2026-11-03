# Tasks — SPEC-001

- [x] **T1** — Move the 17 library directories to `src/Libraries/`.
  - Depends on: SPEC-001 approved, inventory verified.
  - Files/projects: `src/Sa`, `src/Sa.*` → `src/Libraries/…` (`git mv`, history preserved).
  - Verification: `find src -maxdepth 1 -name 'Sa.*'` returns nothing but `src/Sa.slnx`;
    `git status` reports renames, not delete+add.

- [x] **T2** — Fix library-internal relative paths (props import, packed readme/icon).
  - Depends on: T1.
  - Files/projects: 17 `.csproj`, `src/Common.NuGet.Properties.xml`.
  - Verification: `dotnet restore src/Sa.slnx` has no missing-import/missing-file error.

- [x] **T3** — Fix solution entries.
  - Depends on: T1.
  - Files/projects: `src/Sa.slnx` (paths + `/Libraries/` solution folder).
  - Verification: `dotnet sln src/Sa.slnx list` lists 48 projects.

- [x] **T4** — Fix consumer references from samples, tests and fixtures.
  - Depends on: T1.
  - Files/projects: `src/Samples/*`, `src/Tests/*`, `src/Tests/Fixtures/*`.
  - Verification: solution builds; `grep -r "\.\.\\\.\.\\Sa\." src/Samples src/Tests` finds only
    `..\..\Libraries\Sa.…`.

- [x] **T5** — Fix tooling and CI paths.
  - Depends on: T1.
  - Files/projects: `build-sh/tasks.sh`, `build/tasks.ps1`, `build-sh/package-check.sh`,
    `.github/workflows/dotnet.yml`.
  - Verification: `do-package.sh` and `do-package-check.sh` succeed; `bash -n` on edited scripts.

- [x] **T6** — Update documentation and the inventory.
  - Depends on: T1–T5.
  - Files/projects: `README.md`, `README-ru.md`, `AGENTS.md`, `docs/repository/project-structure.md`,
    `docs/architecture/target-architecture.md`, `docs/development/migration-plan.md`.
  - Verification: link check over all Markdown; no `src/Sa.*` path outside `specs/000-…` history.

- [x] **T7** — Full verification against the baseline.
  - Depends on: T2–T6.
  - Files/projects: whole solution.
  - Verification: `do-build.sh`, `do-test.sh`, `do-package.sh`, `do-package-check.sh` — all green with
    the baseline totals and the same 16 package IDs/versions.

- [x] **T8** — Close the spec.
  - Depends on: T7.
  - Files/projects: `specs/001-src-libraries-layout/{spec,acceptance,plan}.md`, SPEC-000 change log.
  - Verification: every acceptance criterion ticked with evidence; spec status `Implemented`.

## Completion checks

- [x] Every acceptance criterion mapped to verification.
- [x] Tests/build/package run for affected projects.
- [x] Docs and spec match actual behavior.
- [x] Public API and NuGet metadata checked where relevant.
