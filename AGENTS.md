# AGENTS.md — Sa

Compact guidance for OpenCode. The root `README.md` / `README-ru.md` are the human-facing map of the libraries; the API-level docs are each `src/Libraries/[<Group>/]<Lib>/Readme.md`. Agent skills live only in `.opencode/skills/` (git-ignored).

## What this is
A monorepo of independent **Native AOT-compatible** .NET libraries (`Sa.*`) for distributed-systems infrastructure. Not a single app — each package builds/packs/publishes on its own.

- Root solution: `src/Sa.slnx` (XML `.slnx`, not `.sln`).
- `src/Libraries/` holds 18 projects: **17 packable packages** grouped by domain in `Configuration/`, `Data/`, `Hybrid/`, `Media/`, `Misc/`, `Outbox/`, plus the shared-source base `Sa` (not packed). The packaging list itself is `PROJECTS` in `build-sh/tasks.sh`.
- `src/Samples/` — 10 runnable examples; `src/Tests/` — 19 test projects + 3 fixture projects; `src/Benchmarks/` — 2 benchmark projects. Shared props/config files stay directly in `src/` (MSBuild/NuGet discover them by walking up from each project).
- Requires **.NET 10 SDK** 10.0.404 or newer in the same feature band (pinned in both `global.json` files and in CI). 10.0.404 ships ILCompiler 10.0.15; older SDKs (e.g. 10.0.401 with ILCompiler 10.0.12) fail restore with NU1903 / GHSA-p3jp-9v83-76w9 in AOT samples. Everything targets `net10.0` — a .NET 9 SDK cannot build it.
- **Central Package Management**: all versions live in `src/Directory.Packages.props`. In a `.csproj`, add `<PackageReference Include="X" />` with **no `<Version/>`**.

## Source file conventions
- Text files are UTF-8 **without a BOM** and use **LF** line endings. `.editorconfig` (`charset = utf-8`, `end_of_line = lf`) and `.gitattributes` (`* text=auto eol=lf` plus per-extension rules) enforce this — do not commit CRLF or a BOM (a BOM breaks shell shebangs).
- The **root** `.editorconfig` is the only one: a nested `*.editorconfig` sets its own `root = true` and silently overrides every rule above it. Do not add one.
- C# style rules live in that same file and are enforced in the build (`EnforceCodeStyleInBuild` + `TreatWarningsAsErrors`). Private fields are `_camelCase` (static ones PascalCase), interfaces `I*`, type parameters `T*`. `dotnet format src/Sa.slnx --no-restore --verify-no-changes` must stay green.

## Public API tracking
- Every library carries `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` (`Microsoft.CodeAnalysis.PublicApiAnalyzers`). A new or changed public member is **RS0016** until its signature is added to `PublicAPI.Unshipped.txt`; a removed one is **RS0017**.
- The shipped list is the released surface: when a version is published, move its `Unshipped` entries into `Shipped`.
- `Version` is **0.21.0** in `src/Libraries/Directory.Build.props`, and `PackageValidationBaselineVersion` pins the last published release (`0.20.0`). Pin the baseline to the new version once that release is published, so a breaking change then fails `dotnet pack` (`EnablePackageValidation`).
- An intentional break inside a `0.x` minor bump is recorded per project in `CompatibilitySuppressions.xml` (generated with `dotnet pack -p:ApiCompatGenerateSuppressionFile=true`, committed with a comment saying why); the baseline stays pinned, so everything else is still guarded.

## Commands

| Goal | Command |
|------|---------|
| Build solution | `dotnet build src/Sa.slnx -c Release`  ·  or `./build-sh/do-build.sh` (clean+restore+build) |
| Run **one** test project | `dotnet run --project src/Tests/<Name>`  |
| Check formatting | `dotnet format src/Sa.slnx --no-restore --verify-no-changes` |
| Run **all** tests | `dotnet test --max-parallel-test-modules 2 --solution src/Sa.slnx --no-build --no-restore` (any cwd)  ·  or `./build-sh/do-test.sh` (builds first, then caps parallelism) |
| **Full gate** (all tests, as CI sees them) | `dotnet build src/Sa.slnx -c Debug --no-restore` **then** the all-tests command above — the build configuration and the run must match, see *The full gate must build the configuration it tests* |
| Skip local-infra suites | `test_ci()` in `build-sh/tasks.sh` runs `dotnet test ... --filter "Category!=Local"`. It has **no** entry-point script and the GitHub workflow does not use it (CI runs the full suite); 4 test files carry `[Trait("Category", "Local")]`, all in `Sa.Configuration.PostgreSqlTests`. |

Build/test helpers live in **`build-sh/`** (POSIX bash, `tasks.sh` holds the shared functions). Entry points: `do-build.sh`, `do-test.sh`, `do-package.sh`, `do-package-check.sh`, `do-push-local.sh`, `do-push-prod.sh`, `do-push-prod-ex.sh`, `do-docker-images.sh`, `do-docker-bypass.sh`, `do-secrets.sh`, `do-fix-max-path-linux.sh`.

### `global.json` lives at the repository root
`global.json` sets `"test": { "runner": "Microsoft.Testing.Platform" }`. It used to live in `src/`, which meant `dotnet test` only picked up the MTP driver when the current directory was `src/` or below; a run from the repo root failed with:

```
error: Testing with VSTest target is no longer supported by Microsoft.Testing.Platform on .NET 10 SDK and later...
```

It now sits at the repository root, so it is discovered from any directory inside the repo. `dotnet` walks up from the current directory, so both shapes work:

- From repo root: `dotnet test --project src/Tests/<Name> -c Release --no-build --no-restore` ✅
- From `src/`: `dotnet test --project Tests/<Name> -c Release --no-build --no-restore` ✅
- `dotnet run --project src/Tests/<Name>` ✅ (MTP exe; works from any cwd)
- `./build-sh/do-test.sh` still `cd`s into `src/` before `dotnet test`; that is harmless.

⚠️ `src/Tests/global.json` is a leftover **identical copy** of the root file: it only ever applies when the working directory is inside `src/Tests`. Keep the two in sync, or delete the stray one — do not let them diverge, because the copy silently wins there.

MTP CLI args go after `--`: simple filters `-class "FQN"`, `-method "FQN.Type.Method"`, `-namespace "name"`, `-displayName "name"`, `-trait "name=value"` (append `-` to exclude, e.g. `-class-`; wildcard `*` at start/end of the value OK). Repeating one filter type = OR, mixing different types = AND; you cannot mix simple and query filtering. Query filter: `-filter "/assembly/namespace/class/method[trait=value]"` (xunit query filter language). `-list <option>` lists tests/assembly info; `--help` shows the full list. Non-zero exit code on failure.

### ⚠️ Test load is capped on purpose — do not "optimize" it away
Every integration test class gets its own Testcontainers PostgreSQL (and SeaweedFS for the S3 suites), so the *unlimited* default is a dozen-plus live databases inside a single assembly and 19 assemblies' worth across the solution. On a 16 GB dev box that exhausts RAM, and the Testcontainers assemblies start failing with resource errors that look like product bugs.

Two independent caps, one per level:

| Level | Mechanism | Where |
|---|---|---|
| Within an assembly | xUnit `maxParallelThreads: 2` — caps concurrently running test *collections* (one collection per class) | `src/Tests/xunit.runner.json`, copied to every test output by `src/Tests/Directory.Build.props` |
| Across assemblies | `dotnet test --max-parallel-test-modules 2` | `build-sh/tasks.sh` → `test_run()`; override per machine with `SA_TEST_PARALLELISM=6 ./build-sh/do-test.sh` |

Raise both on CI / beefier machines. `--max-parallel-test-modules` is only honoured while `dotnet test` stays on the MTP driver, and several ordinary-looking things quietly knock the invocation off that path — most of them die with `MSBUILD : error MSB1001: Unknown switch` once the switch is no longer consumed in leading position:

- **`--solution` must come after it.** Argument order is significant; the switch is consumed only in leading position.
- **No implicit restore.** `--no-restore` is required — otherwise `dotnet test` appends `-restore`. Combined with `--no-build` this keeps NuGet off the critical path entirely, which also avoids a restore stalling behind a proxy.
- **Verbosity: `-v n` is fine for a single-project run; keep it out of the capped run.**
  - ✅ `dotnet test --project Tests/<Name> -c Release --no-build --no-restore -v n`
    stays on the MTP driver and prints the MTP summary (this is the CI shape on the Windows leg).
    A failing test is named with its exception and stack, so a failure on an opaque runner is attributable.
    (With `-v` present the CLI rejects a *bare directory* target — `error: Specifying a directory for 'dotnet test' should be via '--project' or '--solution'` — which is why the target is explicit here.)
  - ❌ `dotnet test --max-parallel-test-modules N --solution Sa.slnx ... -v n`
    is SDK-dependent. On 10.0.112 it rerouted the CLI to the MSBuild driver and died with `MSB1001`; on 10.0.403 (the SDK used when this was verified) the same command starts on the MTP driver with the module cap apparently applied. Verified 2026-11-18. Either way `test_run()` and both CI legs pass no `-v` — `-v n` stays reserved for the single-project shape above.
  - ⚠️ `--logger "console;verbosity=detailed"` is VSTest-only. On MTP it produces
    `Zero tests ran` and exit code 5 (invalid command-line arguments, per the MTP exit-code table).
    There is no VSTest-style logger for MTP — the runner's own console output already names each
    failing test with exception and stack.
- **No `--filter`.** It becomes the MSBuild property `VSTestTestCaseFilter`, with the same effect. That is why `test_ci()` passes `--filter` and no module cap. `testconfig.json` (`commandLineOptions`) is not an escape hatch here — the SDK's MTP build rejects `--config-file`.
- **`MSBUILDDISABLENODEREUSE=1` when a build ran earlier in the same session.** A warm MSBuild worker node swallows the switch even when the command line is correct — the symptom is identical `MSB1001` on an invocation that worked moments before. `test_run()` sets it.

`test_run()` therefore splits the work explicitly: `dotnet build --no-restore`, then `MSBUILDDISABLENODEREUSE=1 dotnet test --no-build --no-restore --max-parallel-test-modules N`. It restores first only when `.packages` (repository root) is missing, so a warm checkout never touches the network. If a new package version lands in `src/Directory.Packages.props`, the build fails with a NuGet "run a restore" error — run `./build-sh/do-build.sh`. The CI workflow already uses the same restore-then-`--no-restore`-build split.

### ⚠️ Exit code 8 = "a module discovered no tests" — and why the fixtures must stay out of discovery
On SDK 10.0.112 a full `dotnet test --solution Sa.slnx` used to end with `Test run summary: Failed!`, `error: 3`, exit code 8 (MTP: "the session discovered no tests") although **every** real test assembly reported `passed` and the summary's `failed:` was 0. Cause: the three fixture projects under `src/Tests/Fixtures/` (`Sa.Fixture`, `Sa.Data.PostgreSql.Fixture`, `Sa.Data.S3.Fixture`) were discovered as test modules and each reported `Zero tests ran`. Fixed by `<IsTestingPlatformApplication>false</IsTestingPlatformApplication>` (and `<IsTestProject>false</IsTestProject>`) in `src/Tests/Fixtures/Directory.Build.props` — fixtures are referenced *by* test projects, they are not test modules themselves. Do not drop that property, and do not "fix" the symptom with `TESTINGPLATFORM_EXITCODE_IGNORE=8` in CI: that would also swallow an empty *real* test assembly, i.e. a whole suite silently not running. If exit 8 ever reappears, look for a project that has no tests but is still being discovered. (The .NET 11 SDK no longer fails the whole run for a single empty module.)

### ⚠️ The full gate must build the configuration it tests
`dotnet test` defaults to **Debug**, and with `--no-build` it runs whatever binaries already sit in `src/artifacts/bin/<project>/debug`. A fix verified with `dotnet build -c Release` or `dotnet run -c Release --no-build` is therefore **not** in the bits the gate runs: it re-runs the old code, the same test fails on the same line, and the failure reads like a flaky test while it is a stale binary. A WorkQueue fix looked flaky for two consecutive full-suite runs this way before the Debug rebuild.

Gate recipe — build and test the same configuration:

```bash
dotnet build src/Sa.slnx -c Debug --no-restore
MSBUILDDISABLENODEREUSE=1 dotnet test --max-parallel-test-modules 2 --solution src/Sa.slnx --no-build --no-restore
```

`-c Release` is equally fine as long as **both** commands use it (the runner then looks under `artifacts/bin/<project>/release`). A green gate ends with `exit=0` and `failed: 0`; exit code 2 means test failures. A few skips are normal and by design: `Sa.Data.TempFolderTests.CleanupTests.Cleanup_PrefixMatch_FollowsThePlatformCaseRule` (`Assert.Skip` off Windows), plus `Assert.Skip` guards for symlinks and FFmpeg codec probes on platforms that lack them.

## MSBuild structure
- Settings live in a `Directory.Build.props` hierarchy; projects do **not** import shared `.props` files manually:
  - `src/Directory.Build.props` — universal: `net10.0`, `ImplicitUsings`, `Nullable`, analyzers (`AnalysisMode=Recommended`) with `TreatWarningsAsErrors=true`, repo metadata, `ArtifactsPath=src/artifacts`, `IsPackable=false`, CI-only `ContinuousIntegrationBuild`.
    A warning is a build failure everywhere (libraries, tests, samples, benchmarks). Deliberate exceptions must be narrow and documented: a scoped `#pragma`/`SuppressMessage` with a written justification, or a `NoWarn` entry in `src/Tests/Directory.Build.props` for a rule that targets shipped API shape rather than test code.
  - `src/Libraries/Directory.Build.props` — libraries: `IsPackable=true`, shared `<Version>` (`0.21.0`), `IsAotCompatible=true` (not `PublishAot`), `EnableConfigurationBindingGenerator=true`, `TrimmerSingleWarn=false`, `VerifyReferenceTrimCompatibility`/`VerifyReferenceAotCompatibility`, `GenerateDocumentationFile=true` (XML docs ship in the package), SourceLink + snupkg, package validation with `PackageValidationBaselineVersion`, per-package `Readme.md` + shared `icon.png` packed, shared refs (Logging.Abstractions, DI.Abstractions, PublicApiAnalyzers, SourceLink). Set `<Description>` and `<PackageTags>` (and an optional per-project `<Version>`) in a library `.csproj`.
  - `src/Libraries/Directory.Build.targets` — packs each library's own `Readme.md` (must be a *targets* file: the SDK's default `None` glob exists only after the props are imported, so an `Update` there would match nothing).
  - `src/Tests/Directory.Build.props` — test host: `OutputType=Exe`, `IsTestProject=true`, `xunit.v3`, MTP, `xunit.runner.json` copy, global `using Xunit`, documented `NoWarn` list for test-only code.
  - `src/Tests/Fixtures/Directory.Build.props` — `IsTestProject=false` + `IsTestingPlatformApplication=false` for fixture projects.
  - `src/Samples/Directory.Build.props` / `src/Benchmarks/Directory.Build.props` — executable, non-packable, not test modules. Most sample `.csproj` files additionally set `PublishAot=true`.
- Nested `Directory.Build.props` files must import the parent explicitly via `$([MSBuild]::GetPathOfFileAbove(...))` — MSBuild only auto-imports the nearest one.
- Central Package Management: `src/Directory.Packages.props` holds shared versions (`MicrosoftExtensionsVersion` property for the family), `src/Tests/Directory.Packages.props` imports it and adds test-only packages. `CentralPackageVersionOverrideEnabled=false` — do not use `VersionOverride`; add/change the version centrally.
- `GeneratePackageOnBuild` is **off**: `dotnet build` never produces `.nupkg`. Packaging happens only through `./build-sh/do-package.sh` → `dotnet pack` (17 `.nupkg` + 17 `.snupkg` into `dist/`).

### ⚠️ Shared source is *linked*, not referenced
`src/Libraries/Sa/Classes/*.cs` and `src/Libraries/Sa/Extensions/*.cs` are pulled into 10 packages via `<Compile Include="..\..\Sa\...cs" Link="..." />` — Sa.Configuration, Sa.Data.PostgreSql, Sa.Data.TempFolder, Sa.HybridFileStorage.Postgres, Sa.HybridFileStorage.S3, Sa.Partitional.PostgreSql, Sa.Schedule, Sa.Outbox, Sa.Outbox.PostgreSql, Sa.Outbox.Redis. Consequences:
- Editing a shared file in `src/Libraries/Sa/` changes code compiled into **several packages at once** — rebuild/retest the affected consumers.
- A new shared type must be **link-`Include`d into each consuming project** or it won't be visible there (it is *not* a normal project reference).
- Don't also glob/`Compile` the same file directly, or you'll get duplicate-type errors.

Dependency graph (who references whom): `Sa.Utils.WorkQueue` → `Sa.Schedule` → `Sa.Outbox` → `Sa.Outbox.PostgreSql`; `Sa.Partitional.PostgreSql` → `Sa.Schedule` + `Sa.Data.PostgreSql`; `Sa.HybridFileStorage.S3` → `Sa.Data.S3` + base.

## Skills and tooling
- Skills live in `.opencode/skills/<name>/SKILL.md` and are **git-ignored**. Only four exist: `grilling`, `grill-me`, `grill-with-docs`, `domain-modeling`, vendored from <https://github.com/mattpocock/skills> (MIT, upstream commit and local deltas pinned in `.opencode/skills/_upstream.md`). There is no `xunit3`/migration skill here — use the official xUnit v3 docs instead.
- `grill-me` / `grill-with-docs` are explicit-invocation only (`permission.skill.* = "ask"` in `opencode.jsonc`); `grilling` / `domain-modeling` stay auto-invocable.
- `/grill-me` interviews about an idea and writes nothing; `/grill-with-docs` does the same and records terms in `GLOSSARY.md`. The repository keeps no `docs/`, `specs/` or ADR folder — decisions live in commit messages and the glossary. `grill-with-docs` needs Build mode — it writes files.
- `.opencode/skills` and `.secrets/` are git-ignored. `opencode.jsonc` also wires a local **postgres MCP server** whose connection string is read from `.secrets/postgres-uri` (local dev database, never the shared/production one).

## Tests
- **xUnit v3** on Microsoft.Testing.Platform (test projects are executables). Not classic xUnit: `Assert.Skip(...)` replaces `Skip=`, and the v3 API differs from v2 — check the xUnit v3 docs (or the v3 migration guide) before porting test code.
- Cancellation token comes from **`TestContext.Current.CancellationToken`** (house pattern: a static `TestToken` property on the test class).
- Integration tests use **Testcontainers** (PostgreSQL + SeaweedFS + Redis) via fixtures in `src/Tests/` — **Docker must be running** for those suites. One container per test class, so concurrency is deliberately throttled — see *Test load is capped on purpose* above.
- Images are pulled **once** and then reused from the local daemon; after that the suites need no registry. `./build-sh/do-docker-images.sh {status|pull|save|load}` warms a machine up, and `save`/`load` moves the tars to one without registry access.
- The S3 server image is **SeaweedFS** (`chrislusf/seaweedfs:4.50`), **not MinIO**: `minio/minio` no longer answers anonymous pulls on Docker Hub, and `quay.io/minio/minio` rejects them too. The fixture runs `mini` (master+volume+filer+S3 in one process) on port 8333 with `-s3.autoCreateBucket=false`, so a missing bucket fails like it did on MinIO — see `S3FixtureSettings`.
- If a VPN tunnel owns the default route, pulls crawl. `sudo ./build-sh/do-docker-bypass.sh {on|off|status}` routes the registry and its CDN around the tunnel; see `build-sh/readme.md`.
- `test_ci()` runs `--filter "Category!=Local"`; 4 test files carry `[Trait("Category", "Local")]` (all in `Sa.Configuration.PostgreSqlTests`). Nothing invokes `test_ci()` at the moment — the GitHub workflow runs the full suite on Linux and `Sa.Media.FFmpegTests` on Windows.
- Each lib declares `<InternalsVisibleTo Include="<Name>.Tests" />`, so tests can touch `internal` members.

## NuGet / packaging quirks
- `nuget.config` at the repository root sets the global package cache to **`.packages`** (not the default `~/.nuget/packages`) and maps `Sa.*` to a local folder feed `./src/nupkgs` (nuget.org for everything else, and `auditSources` points at nuget.org because a folder feed carries no advisories). Because it sits at the root, restore resolves the same config from any cwd.
