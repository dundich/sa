# Tasks — SPEC-002

- [x] **T1** — Real clean in `tasks.sh`.
  - Depends on: —
  - Files: `build-sh/tasks.sh` (`_msbuild_clean`, `clean`).
  - Verification: `do-build.sh` prints a `Clean solution` step that runs `dotnet clean`; a sentinel
    file in `src/Libraries/Sa/obj/` is gone after the run.

- [x] **T2** — `nu_push` hardening.
  - Depends on: —
  - Files: `build-sh/tasks.sh` (`nu_push`).
  - Verification: with an empty `dist/`, push exits non-zero and names `do-package.sh`; with a key,
    `--api-key` is passed.

- [x] **T3** — Local push to `src/nupkgs`.
  - Depends on: T2.
  - Files: `build-sh/do-push-local.sh`.
  - Verification: `./build-sh/do-push-local.sh` pushes 16+16 packages into `src/nupkgs` with no
    pre-registered `local` source.

- [x] **T4** — Prod push requires `NUGET_API_KEY`.
  - Depends on: T2.
  - Files: `build-sh/do-push-prod.sh`.
  - Verification: without the variable → exit 1 with a clear message and no push attempt.

- [x] **T5** — Long-path helper targets the repo root.
  - Depends on: —
  - Files: `build-sh/do-fix-max-path-linux.sh`.
  - Verification: computed `TARGET` equals the repository root when invoked from `build-sh/`.

- [x] **T6** — Documentation.
  - Depends on: T1–T5.
  - Files: `build-sh/readme.md`.
  - Verification: readme matches the new behavior; no stale `local` source instructions.

- [x] **T7** — Regression check.
  - Depends on: T1–T6.
  - Files: whole `build-sh/`.
  - Verification: `bash -n` on all scripts; `do-build.sh`, `do-test.sh`, `do-package.sh`,
    `do-package-check.sh`, `do-secrets.sh check` all green; `do-push-prod-ex.sh` unchanged
    (`git status`).

## Completion checks

- [x] Every acceptance criterion mapped to verification.
- [x] Tests/build/package run for affected projects.
- [x] Docs and spec match actual behavior.
- [x] Public API and NuGet metadata checked where relevant (N/A — tooling only).
