# Plan — SPEC-002

## Steps

1. `build-sh/tasks.sh` — add a real clean step and harden `nu_push` (empty `dist/`, optional key).
2. `build-sh/do-push-local.sh` — push to `src/nupkgs`.
3. `build-sh/do-push-prod.sh` — require `NUGET_API_KEY`, fail fast, pass `--api-key`.
4. `build-sh/do-fix-max-path-linux.sh` — derive the target from the script location.
5. `build-sh/readme.md` — document clean semantics, local feed, prod key.
6. Verify: `bash -n`, `do-build.sh` (clean visible), `do-package.sh`, `do-package-check.sh`,
   `do-push-local.sh` into a throwaway feed, `do-push-prod.sh` without a key (must fail fast),
   `do-fix-max-path-linux.sh` dry check of the computed target, `do-secrets.sh check`.

## Affected files

- `build-sh/tasks.sh` (`_msbuild_clean`, `clean`, `nu_push`)
- `build-sh/do-push-local.sh`, `build-sh/do-push-prod.sh`, `build-sh/do-fix-max-path-linux.sh`
- `build-sh/readme.md`
- `specs/002-build-script-fixes/*`

Not touched: `build-sh/do-push-prod-ex.sh`, `build/` (PowerShell), `src/nuget.config`, CI workflow.

## Verification notes

- `do-push-local.sh` will write into `src/nupkgs` during verification; those files are git-ignored and
  are removed afterwards so a stale local feed cannot shadow future builds.
- Prod push is verified only for the “no key” path; an actual nuget.org push is out of scope.
- `/sa` symlink is not created (needs root); the computed target is asserted instead.

## Risks

- A real `dotnet clean` makes `do-build.sh` slower on a cold tree; this is the documented contract.
- `--api-key` on the command line is visible in the local process list; it is the only way
  `dotnet nuget push` accepts it without a NuGet.config `<apikeys>` entry. The env var is not echoed.
- Pushing both `.nupkg` and `.snupkg` to a folder feed is unchanged behavior.

## Rollback

Revert the commit; scripts are developer tooling with no runtime or packaging impact.
