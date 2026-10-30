# Acceptance criteria — SPEC-004

Verified 2026-10-30 on the working tree after SPEC-001/002/003 (branch `sdd`).

- [ ] **AC-1** — The 3 infrastructure standalone projects live in `src/Libraries/Misc/`; the shared-source
  base `Sa` stays directly in `src/Libraries/`; no project, assembly or namespace was renamed.
  Evidence: `find src/Libraries -maxdepth 2 -type d` → the `Misc/` group contains the 3 projects; `Sa` at the
  root. `git status` reports every moved path as a rename (`R`).

- [ ] **AC-2** — Every path-dependent reference resolves: props imports, `ProjectReference`s,
  `<Compile Include>` links, samples/tests/benchmarks/fixtures references, packaging paths, CI and
  `.gitignore`.
  Evidence: scripted resolution of every `ProjectReference` / `Compile` / `Import` in all `.csproj` →
  `unresolved 0`.

- [ ] **AC-3** — `dotnet build` matches the baseline: 0 warnings, 0 errors, 48 projects.

- [ ] **AC-4** — Test totals match the baseline.

- [ ] **AC-5** — Packages are unchanged: 16 IDs at the same versions, 16 `.snupkg`.

- [ ] **AC-6** — `do-package-check.sh` is green.

- [ ] **AC-7** — The solution groups the standalone projects under `/Libraries/Misc/`.

- [ ] **AC-8** — Documentation matches the tree and has no broken links.

- [ ] **AC-9** — Packaging no longer assumes a group for these projects.

## Regression checks

- [ ] `bash -n` on all `build-sh/*.sh`.
- [ ] `./build-sh/do-build.sh` — exit `0`, 0 warnings / 0 errors.
- [ ] `./build-sh/do-test.sh` — exit `0`, 2712 / 2711 / 1 / 0.
- [ ] `./build-sh/do-package.sh` — exit `0`, 16 + 16 packages, versions unchanged.
- [ ] `./build-sh/do-package-check.sh` — exit `0`.
- [ ] Markdown link check — 0 broken links (excluding historical `specs/000`–`specs/003`).
- [ ] No functional, formatting or version change mixed into the move.
