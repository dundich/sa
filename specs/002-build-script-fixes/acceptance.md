# Acceptance criteria — SPEC-002

Verified 2026-10-30 on the working tree after SPEC-001.

- [x] **AC-1** — `clean()` runs `dotnet clean` before `dotnet build`; `dist/` is still wiped.
  Evidence: `./build-sh/do-build.sh` log shows `===== Clean folder …/dist =====`, then
  `===== Clean solution =====` with `Clean target(s)` and a long `Deleting file …/bin/Release/…` list,
  then `Restore`, then `Build`. Exit `0`.

- [x] **AC-2** — `do-push-local.sh` pushes into `src/nupkgs` without a pre-registered source.
  Evidence: `./build-sh/do-push-local.sh` → exit `0`; `src/nupkgs` contained the 16 `.nupkg` files.
  No `local` source exists in `dotnet nuget list source` (only `nuget.org`).

- [x] **AC-3** — `do-push-prod.sh` fails fast without `NUGET_API_KEY` and passes it as `--api-key`
  when present; the key is never echoed.
  Evidence: without the variable → exit `1`, message
  `ERROR: NUGET_API_KEY не задан` + hint about `do-push-prod-ex.sh`, no push attempted.
  Code path: `nu_push "https://api.nuget.org/v3/index.json" "$NUGET_API_KEY"` → `--api-key`.

- [x] **AC-4** — `nu_push` fails when `dist/` has no packages.
  Evidence: with `dist/` temporarily emptied → exit `1`,
  `ERROR: в …/dist нет пакетов — сначала ./build-sh/do-package.sh`.

- [x] **AC-5** — `do-fix-max-path-linux.sh` resolves the repository root from its own location.
  Evidence: invoked from `build-sh/`, the computed target is
  `/home/du/Documents/sources/github/dundich/sa` (equals `cd .. && pwd`). `/sa` itself was not created
  (requires root; out of scope for verification).

- [x] **AC-6** — `build-sh/readme.md` matches the new behavior.
  Evidence: usage block shows `NUGET_API_KEY=... ./do-push-prod.sh` and the `src/nupkgs` feed; “Notes”
  documents the real clean, the local feed path, the API-key requirement and the empty-`dist/` error.

- [x] **AC-7** — `do-push-prod-ex.sh` is unmodified.
  Evidence: `git status --short build-sh` does not list it; `git diff --stat build-sh` has 7 files,
  none of them `do-push-prod-ex.sh`.

## Regression checks

- [x] `bash -n` on every `build-sh/*.sh` — no syntax errors.
- [x] `./build-sh/do-build.sh` — exit `0`, 0 warnings / 0 errors.
- [x] `./build-sh/do-package.sh` — exit `0`, 16 `.nupkg` + 16 `.snupkg`.
- [x] `./build-sh/do-package-check.sh` — exit `0`, `Package payload OK (0.17.0)`.
- [x] `./build-sh/do-secrets.sh check` — exit `0`, `No encrypted secrets files found.`
- [x] `./build-sh/do-test.sh` — exit `0`, `total: 2712`, `succeeded: 2711`, `skipped: 1`, `failed: 0`
  (identical to the SPEC-000 baseline).
- [x] Verification artifacts removed: `src/nupkgs` back to `.gitkeep` only; no files under `dist/`
  are tracked.
