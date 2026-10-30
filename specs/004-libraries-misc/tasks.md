# Tasks — SPEC-004

- [x] **T1** — Create the `Misc/` group folder and `git mv` the 3 standalone projects.
  - Depends on: —
  - Files: `src/Libraries/Misc/{Sa.Partitional.PostgreSql,Sa.Schedule,Sa.Utils.WorkQueue}`.
  - Verification: `src/Libraries` contains `Sa` + 6 folders; `git status` reports renames.

- [x] **T2** — Rewrite relative references in the moved `.csproj` (props import depth +2 levels, shared-source
  links +1 level).
  - Depends on: T1.
  - Files: `src/Libraries/Misc/{Sa.Partitional.PostgreSql,Sa.Schedule,Sa.Utils.WorkQueue}/*.csproj`.
  - Verification: scripted resolution check → 0 unresolved references.

- [x] **T3** — Rewrite `src/Sa.slnx` (move the 3 projects into the new `/Libraries/Misc/` solution folder).
  - Depends on: T1.
  - Files: `src/Sa.slnx`.
  - Verification: `dotnet sln src/Sa.slnx list` → 48 projects, grouped paths.

- [x] **T4** — Rewrite cross-group references in consumers (Outbox, Hybrid), samples, tests, benchmarks.
  - Depends on: T1–T3.
  - Files: `src/{Libraries,Outbox,Hybrid,Samples,Tests,Benchmarks}/**/*.csproj`.
  - Verification: scripted resolution check → 0 unresolved references.

- [x] **T5** — Documentation update.
  - Depends on: T1–T4.
  - Files: `README.md`, `README-ru.md`, `AGENTS.md`, `docs/repository/project-structure.md`,
    `docs/architecture/target-architecture.md`, `docs/development/migration-plan.md`.
  - Verification: link check → 0 broken; no live doc points at a removed path.

- [ ] **T6** — Regression verification.
  - Depends on: T2–T5.
  - Files: whole solution.
  - Verification: build 0/0, tests 2712/2711/1/0, 16+16 packages, package check green.

## Completion checks

- [ ] Every acceptance criterion mapped to verification.
- [ ] Tests/build/package run for affected projects.
- [ ] Docs and spec match actual behavior.
- [ ] Public API and NuGet metadata checked where relevant.
