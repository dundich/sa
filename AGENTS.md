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
| Build solution | `dotnet build src/Sa.slnx -c Release`  ·  or `.\build\do_build.ps1` (clean+restore+build) |
| Run **one** test project | `dotnet run --project src/Tests/<Name>`  |
| Run **all** tests | `dotnet test src/Sa.slnx` **run from inside `src/`**  ·  or `.\build\do_test.ps1` |
| CI-style test run (skip local-infra) | `dotnet test src/Sa.slnx --filter "Category!=Local"` |


### ⚠️ `dotnet test` only works from inside `src/`
`src/global.json` sets `"test": { "runner": "Microsoft.Testing.Platform" }`. This file is only discovered when the current directory is `src/` or below. **Running `dotnet test <project>` from the repo root fails** with:

```
error: Testing with VSTest target is no longer supported by Microsoft.Testing.Platform on .NET 10 SDK and later...
```

- From `src/`: `dotnet test Tests/<Name>` ✅
- From repo root: `dotnet run --project src/Tests/<Name>` ✅ (MTP exe; works from any cwd)
- `.\build\do_test.ps1` already `Push-Location`s into `src/` before `dotnet test`, so it works from any cwd (`build/tasks.ps1` was fixed to do this).

MTP CLI args go after `--`: `--treenode-filter "*ClassName*"`, `--treenode-filter-type FQN|DisplayName|ClassName`, `-t` (list tests), `--help` (full list). Non-zero exit code on failure.

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
- Integration tests use **Testcontainers** (PostgreSQL + Minio) via fixtures in `src/Tests/Fixtures/` — **Docker must be running** for those suites.
- `TestCi` / CI run `dotnet test ... --filter "Category!=Local"` to skip local-infra tests. As of now **no test carries a `Category` trait** (grep-verified), so this filter currently excludes nothing — it's a placeholder for future `Category=Local` tests.
- Each lib declares `<InternalsVisibleTo Include="<Name>.Tests" />`, so tests can touch `internal` members.

## NuGet / packaging quirks
- `src/nuget.config` sets the global package cache to **`src/.packages`** (not the default `~/.nuget/packages`) and maps `Sa.*` to a local feed `./nupkgs` (nuget.org for everything else).

