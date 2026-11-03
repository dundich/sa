# Plan — SPEC-000

## Scope

Documentation and process only: add the SDD layer, record the verified baseline, and link it from the
root READMEs. No source moves and no runtime changes.

## Steps

1. **Capture baseline** — commit `2b422c9`, branch `sdd`, .NET SDK `10.0.403`, Docker `29.9.2`. ✅
2. **Inventory the checkout** — 48 `.csproj`, project counts, dependency map, linked shared source,
   test surface, path-dependent configuration → `docs/repository/project-structure.md`. ✅
3. **Run the canonical commands** — `./build-sh/do-build.sh`, `./build-sh/do-test.sh`,
   `./build-sh/do-package.sh`; record exit codes, summary lines and the produced package list →
   `baseline.md`. ✅
4. **Pin the canonical SDD layout** — move the SDD documents from `docs/` to the root paths described
   by ADR-0001 (`specs/`, `templates/`, `checklists/`, `docs/decisions/`) and update links. ✅
5. **Fill the spec and acceptance criteria** with the measured facts and the resolved open
   questions. ✅
6. **Link the workflow from the root READMEs** (`README.md`, `README-ru.md`). ✅
7. **Review with maintainers** — the spec stays `In Review` until this happens. ✅ (approved after the
   recorded baseline was reviewed)
8. **Re-run the baseline commands after any later structural change** and compare against
   `baseline.md`. ⏳

## Out of scope (explicitly deferred)

- `src/Libraries/` grouping — needs its own migration spec, move list and rollback plan.
- CI validation of spec format — separate spec (stage 4 of the migration plan).
- Fixing stale narrative in the root README library list (the spec and inventory, not the README,
  are the normative source of package facts).

## Risks

- Baseline results are machine-dependent (Docker available, RAM, parallelism caps). `baseline.md`
  records the environment so a later mismatch can be attributed.
- Integration suites need Docker; on a machine without it only the non-container tests are
  meaningful, and that must not be reported as a green full run.
- New documentation can contradict the older `docs/01..03` assessment; the migration plan is the
  place to reconcile it, not this change.

## Rollback

Revert this documentation-only change: delete `specs/`, `templates/`, `checklists/`,
`docs/decisions/`, `docs/repository/` and the README sections. No production-code rollback is
required, because nothing under `src/` changed.
