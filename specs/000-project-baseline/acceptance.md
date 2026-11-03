# Acceptance criteria — SPEC-000

Baseline anchor: commit `2b422c9`, branch `sdd`, measured 2026-10-30. Evidence commands run from the
repository root unless stated otherwise.

- [x] **AC-1** — A contributor can find the SDD workflow from root `README.md`.
  Evidence: both `README.md` and `README-ru.md` link `docs/README.md`, `specs/`,
  `templates/`, `checklists/` and `docs/decisions/` from a dedicated section.

- [x] **AC-2** — Templates exist for spec, plan and tasks.
  Evidence: `templates/spec.md`, `templates/plan.md`, `templates/tasks.md`.

- [x] **AC-3** — No production source/project paths are moved in this change.
  Evidence: `git status --short` lists only `docs/`, `specs/`, `templates/`, `checklists/`,
  `README.md`, `README-ru.md`. `src/` is untouched; `src/Sa.slnx` is byte-identical.

- [x] **AC-4** — Baseline build, test and package commands are recorded with results.
  Evidence: `specs/000-project-baseline/baseline.md` — same commands, toolchain versions, per-command
  exit codes and package list.

- [x] **AC-5** — Existing package IDs, target frameworks and public API are unchanged.
  Evidence: no change under `src/`; the 16 package IDs and their versions are enumerated in
  `docs/repository/project-structure.md` and in `baseline.md`, all `net10.0`.

- [x] **AC-6** — A repository inventory lists solution/project files and path-dependent configuration.
  Evidence: `docs/repository/project-structure.md` (48 `.csproj`, dependency map, linked shared
  source, test surface, table of path-dependent configuration).

- [x] **AC-7** — A follow-up structural move cannot be marked ready without a move list,
  dependency/reference audit, and rollback plan.
  Evidence: `checklists/repository-move.md` plus SPEC-000 R-3 and the migration plan (stage 3);
  DoR in `checklists/definition-of-ready.md`.

- [x] **AC-8** — Documentation clearly distinguishes verified repository facts from assumptions.
  Evidence: `spec.md` and `project-structure.md` state the commit/toolchain they were verified on;
  `docs/repository/project-assessment.md` remains explicitly an initial assessment; resolved open questions
  are listed in `spec.md` with the decision taken.

## Verification of the documentation-only change

- [x] `git status --short` shows no modified file under `src/`.
- [x] The SDD tree lives at the canonical paths: `specs/`, `templates/`, `checklists/`,
  `docs/decisions/`, `docs/repository/`.
- [x] No build input (`*.props`, `*.targets`, `*.csproj`, `*.slnx`, `nuget.config`) references
  `specs/`, `templates/` or `checklists/`.
