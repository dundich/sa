# Tasks — SPEC-003

- [x] **T1** — Create the 5 group folders and `git mv` the 13 projects.
  - Depends on: —
  - Files: `src/Libraries/{Configuration,Data,Hybrid,Media,Outbox}/*`.
  - Verification: `src/Libraries` contains 4 projects + 5 folders; `git status` reports renames.

- [x] **T2** — Rewrite relative references in all `.csproj`.
  - Depends on: T1.
  - Files: `src/Libraries/**/*.csproj`, `src/{Samples,Tests,Benchmarks}/**/*.csproj`.
  - Verification: scripted resolution check → 0 unresolved references.

- [x] **T3** — Rewrite `src/Sa.slnx`.
  - Depends on: T1.
  - Files: `src/Sa.slnx`.
  - Verification: `dotnet sln src/Sa.slnx list` → 48 projects, grouped paths.

- [x] **T4** — Group-agnostic packaging + tooling paths.
  - Depends on: T1.
  - Files: `build-sh/tasks.sh`, `build/tasks.ps1`, `build-sh/package-check.sh`,
    `.github/workflows/dotnet.yml`, `.gitignore`.
  - Verification: `do-package.sh` and `do-package-check.sh` succeed; `.gitignore` matches the new
    FFmpeg payload paths.

- [x] **T5** — Documentation update.
  - Depends on: T1–T4.
  - Files: `README.md`, `README-ru.md`, `AGENTS.md`, `docs/repository/project-structure.md`,
    `docs/architecture/target-architecture.md`, `docs/development/migration-plan.md`.
  - Verification: link check → 0 broken; no live doc points at a removed path.

- [x] **T6** — Regression verification.
  - Depends on: T2–T5.
  - Files: whole solution.
  - Verification: build 0/0, tests 2712/2711/1/0, 16+16 packages, package check green.

## Completion checks

- [x] Every acceptance criterion mapped to verification.
- [x] Tests/build/package run for affected projects.
- [x] Docs and spec match actual behavior.
- [x] Public API and NuGet metadata checked where relevant.
