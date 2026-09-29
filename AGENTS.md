# AGENTS.md — Sa

Compact guidance for OpenCode. Full library docs live in `README.md` / `QWEN.md` and each `src/<Lib>/Readme.md`; per-library testing/style patterns live in `.github/skills/` and `.qwen/skills/`.

## What this is
A monorepo of independent **Native AOT** .NET libraries (`Sa.*`) for distributed-systems infrastructure. Not a single app — each package builds/packs/publishes on its own.

- Root solution: `src/Sa.slnx` (XML `.slnx`, not `.sln`).
- Requires **.NET 10 SDK** (local: 10.0.x). Everything targets `net10.0` — a .NET 9 SDK cannot build it.
- **Central Package Management**: all versions live in `src/Directory.Packages.props`. In a `.csproj`, add `<PackageReference Include="X" />` with **no `<Version/>`**.

## Commands



| Goal | Command |
|------|---------|
| Build solution | `dotnet build src/Sa.slnx -c Release`  ·  or `./build-sh/do-build.sh` (clean+restore+build) |
| Run **one** test project | `dotnet run --project src/Tests/<Name>`  |
| Run **all** tests | `dotnet test --max-parallel-test-modules 2 --solution Sa.slnx --no-build --no-restore` **run from inside `src/`**  ·  or `./build-sh/do-test.sh` (builds first, then caps parallelism) |
| CI-style test run (skip local-infra) | `dotnet test --solution Sa.slnx --filter "Category!=Local"` |

Build/test helpers live in **`build-sh/`** (POSIX bash, `tasks.sh` holds the shared functions). Entry points: `do-build.sh`, `do-test.sh`, `do-package.sh`, `do-push-local.sh`, `do-push-prod.sh`, `do-fix-max-path-linux.sh`.


### ⚠️ `dotnet test` only works from inside `src/`
`src/global.json` sets `"test": { "runner": "Microsoft.Testing.Platform" }`. This file is only discovered when the current directory is `src/` or below. **Running `dotnet test <project>` from the repo root fails** with:

```
error: Testing with VSTest target is no longer supported by Microsoft.Testing.Platform on .NET 10 SDK and later...
```

- From `src/`: `dotnet test Tests/<Name>` ✅
- From `src/`, single project with detail output (CI Windows leg): `dotnet test --project Tests/<Name> -c Release --no-build --no-restore -v n` ✅ — `-v` rejects a bare directory, so the target must be explicit; see *Test load is capped on purpose* below
- From repo root: `dotnet run --project src/Tests/<Name>` ✅ (MTP exe; works from any cwd)
- `./build-sh/do-test.sh` `cd`s into `src/` before `dotnet test`, so it works from any cwd.

MTP CLI args go after `--`: simple filters `-class "FQN"`, `-method "FQN.Type.Method"`, `-namespace "name"`, `-displayName "name"`, `-trait "name=value"` (append `-` to exclude, e.g. `-class-`; wildcard `*` at start/end of the value OK). Repeating one filter type = OR, mixing different types = AND; you cannot mix simple and query filtering. Query filter: `-filter "/assembly/namespace/class/method[trait=value]"` (xunit query filter language). `-list <option>` lists tests/assembly info; `--help` shows the full list. Non-zero exit code on failure.

### ⚠️ Test load is capped on purpose — do not "optimize" it away
Every integration test class gets its own Testcontainers PostgreSQL (Minio for the S3 suites), so the *unlimited* default is ~13 live databases inside a single assembly and ~19 assemblies' worth across the solution. On a 16 GB dev box that exhausts RAM, and the Testcontainers assemblies start failing with resource errors that look like product bugs.

Two independent caps, one per level:

| Level | Mechanism | Where |
|---|---|---|
| Within an assembly | xUnit `maxParallelThreads: 2` — caps concurrently running test *collections* (one collection per class) | `src/Tests/xunit.runner.json`, copied to every test output by `src/Tests/Host.Test.Properties.xml` |
| Across assemblies | `dotnet test --max-parallel-test-modules 2` | `build-sh/tasks.sh` → `test_run()`; override per machine with `SA_TEST_PARALLELISM=6 ./build-sh/do-test.sh` |

Raise both on CI / beefier machines. `--max-parallel-test-modules` is only honoured while `dotnet test` stays on the MTP driver, and several ordinary-looking things quietly knock the invocation off that path — most of them die with `MSBUILD : error MSB1001: Unknown switch` once the switch is no longer consumed in leading position:

- **`--solution` must come after it.** Argument order is significant; the switch is consumed only in leading position.
- **No implicit restore.** `--no-restore` is required — otherwise `dotnet test` appends `-restore`. Combined with `--no-build` this keeps NuGet off the critical path entirely, which also avoids a restore stalling behind a proxy.
- **Verbosity: `-v n` is fine for a single-project run, fatal for the capped run.**
  - ✅ `dotnet test --project Tests/<Name> -c Release --no-build --no-restore -v n`
    stays on the MTP driver and prints the MTP summary (this is the CI shape on the Windows leg).
    A failing test is named with its exception and stack, so a failure on an opaque runner is attributable.
    (With `-v` present the CLI rejects a *bare directory* target — `error: Specifying a directory for 'dotnet test' should be via '--project' or '--solution'` — which is why the target is explicit here.)
  - ❌ `dotnet test --max-parallel-test-modules N --solution Sa.slnx ... -v n`
    is SDK-dependent — treat it as fatal. On 10.0.112 (verified 2026-09-27, `-v` in both last and leading position) the module cap is still consumed in leading position and the MTP driver runs the full solution. The same command was reported failing with `MSBUILD : error MSB1001: Unknown switch` on another SDK (version not pinned — the `-v` reroutes the CLI to the MSBuild driver, where the module-cap switch is no longer consumed in leading position). Unverified either way for a machine that is not on 10.0.112, so `test_run()` passes no `-v` at all and the CI legs never get it either — `-v n` is reserved for the single-project `--project` shape above.
  - ⚠️ `--logger "console;verbosity=detailed"` is VSTest-only. On MTP it produces
    `Zero tests ran` and exit code 5 (invalid command-line arguments, per the MTP exit-code table).
    There is no VSTest-style logger for MTP — the runner's own console output already names each
    failing test with exception and stack.
- **No `--filter`.** It becomes the MSBuild property `VSTestTestCaseFilter`, with the same effect. That is why `test_ci()` passes `--filter` and no module cap. `testconfig.json` (`commandLineOptions`) is not an escape hatch here — the SDK's MTP build rejects `--config-file`.
- **`MSBUILDDISABLENODEREUSE=1` when a build ran earlier in the same session.** A warm MSBuild worker node swallows the switch even when the command line is correct — the symptom is identical `MSB1001` on an invocation that worked moments before. `test_run()` sets it.

`test_run()` therefore splits the work explicitly: `dotnet build --no-restore`, then `MSBUILDDISABLENODEREUSE=1 dotnet test --no-build --no-restore --max-parallel-test-modules N`. It restores first only when `src/.packages` is missing, so a warm checkout never touches the network. If a new package version lands in `src/Directory.Packages.props`, the build fails with a NuGet "run a restore" error — run `./build-sh/do-build.sh`. The CI workflow already uses the same restore-then-`--no-restore`-build split.

### ⚠️ Exit code 8 = "a module discovered no tests" — and why the fixtures must stay out of discovery
On SDK 10.0.112 a full `dotnet test --solution Sa.slnx` used to end with `Test run summary: Failed!`, `error: 3`, exit code 8 (MTP: "the session discovered no tests") although **every** real test assembly reported `passed` and the summary's `failed:` was 0. Cause: the three fixture projects under `src/Tests/Fixtures/` (`Sa.Fixture`, `Sa.Data.PostgreSql.Fixture`, `Sa.Data.S3.Fixture`) were discovered as test modules and each reported `Zero tests ran`. Fixed by `<IsTestingPlatformApplication>false</IsTestingPlatformApplication>` in those three `.csproj`s — they are referenced *by* test projects, they are not test modules themselves. Do not drop that property, and do not "fix" the symptom with `TESTINGPLATFORM_EXITCODE_IGNORE=8` in CI: that would also swallow an empty *real* test assembly, i.e. a whole suite silently not running. If exit 8 ever reappears, look for a project that has no tests but is still being discovered. (The .NET 11 SDK no longer fails the whole run for a single empty module.)

## MSBuild structure
- Every library imports `src/Common.NuGet.Properties.xml` → `src/Common.Properties.xml`, which sets: `net10.0`, `PublishAot=true`, `IsAotCompatible=true`, `Nullable`, analyzers, SourceLink, `GeneratePackageOnBuild=true`, and shared refs (Logging.Abstractions, DI, SourceLink). Set `<Version>` + `<Description>` in each lib's own `.csproj`.
- Every test project imports `src/Tests/Host.Test.Properties.xml` (Exe output, MTP, `xunit.v3`, global `using Xunit`).

### ⚠️ Shared source is *linked*, not referenced
`src/Sa/Classes/*.cs` and `src/Sa/Extensions/*.cs` are pulled into many packages via `<Compile Include="..\Sa\...cs" Link="..." />` (see Sa.Outbox, Sa.Outbox.PostgreSql, Sa.Partitional.PostgreSql, Sa.Schedule, Sa.Data.PostgreSql, Sa.HybridFileStorage.S3). Consequences:
- Editing a shared file in `src/Sa/` changes code compiled into **several packages at once** — rebuild/retest the affected consumers.
- A new shared type must be **link-`Include`d into each consuming project** or it won't be visible there (it is *not* a normal project reference).
- Don't also glob/`Compile` the same file directly, or you'll get duplicate-type errors.

Dependency graph (who references whom): `Sa.Utils.WorkQueue` → `Sa.Schedule` → `Sa.Outbox` → `Sa.Outbox.PostgreSql`; `Sa.Partitional.PostgreSql` → `Sa.Schedule` + `Sa.Data.PostgreSql`; `Sa.HybridFileStorage.S3` → `Sa.Data.S3` + base.

## Tests
- **xUnit v3** on Microsoft.Testing.Platform (test projects are executables). Not classic xUnit — see the `xunit3` skill (`.opencode/skills/xunit3/`) for the v3 API, and `.github/skills/migrate-xunit-to-xunit-v3/` for the migration patterns.
- Cancellation token comes from **`TestContext.Current.CancellationToken`** (house pattern: a static `TestToken` property on the test class).
- Integration tests use **Testcontainers** (PostgreSQL + Minio) via fixtures in `src/Tests/Fixtures/` — **Docker must be running** for those suites. One container per test class, so concurrency is deliberately throttled — see *Test load is capped on purpose* above.
- `TestCi` / CI run `dotnet test ... --filter "Category!=Local"` to skip local-infra tests. As of now **no test carries a `Category` trait** (grep-verified), so this filter currently excludes nothing — it's a placeholder for future `Category=Local` tests.
- Each lib declares `<InternalsVisibleTo Include="<Name>.Tests" />`, so tests can touch `internal` members.

## NuGet / packaging quirks
- `src/nuget.config` sets the global package cache to **`src/.packages`** (not the default `~/.nuget/packages`) and maps `Sa.*` to a local feed `./nupkgs` (nuget.org for everything else).

