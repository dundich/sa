# build scripts (bash)

Alternative to PowerShell `build/` — these scripts work on Linux/macOS.

## Usage

```bash
# Build from scratch (clean + restore + build)
./do-build.sh

# Run tests
./do-test.sh

# Create NuGet packages
./do-package.sh

# Push packages to local NuGet source
./do-push-local.sh

# Push packages to nuget.org
./do-push-prod.sh

# Enable long paths on Linux (create /sa symlink)
sudo ./do-fix-max-path-linux.sh
```

## Notes

- All scripts use `set -euo pipefail` for strict error handling.
- `dotnet test` runs from the `src/` directory (required by .NET 10 MTP runner).
- Projects to package are defined in `tasks.sh` — same list as the PowerShell version.
- The `local` NuGet source must be configured separately (`dotnet nuget add source ...`).

## Test resource limits

`./do-test.sh` deliberately throttles the run, because every integration test class boots its own
Testcontainers PostgreSQL and the default is to start all of them at once:

- `SA_TEST_PARALLELISM` — how many test assemblies may run at once (default `2`).
  `SA_TEST_PARALLELISM=6 ./do-test.sh` on a beefier machine.
- Per-assembly concurrency is capped separately by `src/Tests/xunit.runner.json`
  (`maxParallelThreads: 2`), which limits how many test classes run at once inside one assembly.

`test_run()` also builds with `--no-restore` and runs the tests with `--no-build --no-restore`,
restoring only when `src/.packages` is missing. That keeps NuGet off the critical path — a restore
can stall behind a proxy. If a new package version lands in `src/Directory.Packages.props` the build
fails with a NuGet "run a restore" error; run `./do-build.sh` once.

Both switches are needed for `--max-parallel-test-modules` to be honoured at all: any implicit
restore, `-v` or `--filter` silently drops `dotnet test` back onto the MSBuild driver, and a warm
MSBuild worker node (hence `MSBUILDDISABLENODEREUSE=1`) does the same. The symptom is always
`MSBUILD : error MSB1001: Unknown switch`. See the "Test load is capped on purpose" section in
`AGENTS.md`.
