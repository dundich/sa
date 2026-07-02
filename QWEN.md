# Sa — .NET 10 Experimental AOT Library Suite

## Project Overview

**Sa** is a collection of reusable .NET 10 libraries focused on infrastructure patterns for distributed systems. It targets **.NET 10.0**, uses **Native AOT**, and follows the **Central Package Management (CPM)** pattern via `Directory.Packages.props`.

### Libraries

| Library | Purpose |
|---|---|
| **Sa** | Shared utility classes (LockRenewer, MurmurHash3, Retry, extensions) consumed by other libs via `<Compile Include="..." Link="..."/>` |
| **Sa.Configuration** | Command-line argument parsing (`Arguments`) and secure secrets management from files/env vars/host key files |
| **Sa.Configuration.PostgreSql** | PostgreSQL-backed dynamic configuration source — changes in DB reflect in-app without redeploy |
| **Sa.Data.PostgreSql** | Lightweight Npgsql client wrapper |
| **Sa.Data.S3** | S3 data client (Minio-compatible) |
| **Sa.HybridFileStorage** | Hybrid file storage abstraction with automatic provider failover (FileSystem ↔ S3 ↔ Postgres) |
| **Sa.HybridFileStorage.FileSystem** | FileSystem provider implementation |
| **Sa.HybridFileStorage.S3** | S3 provider implementation |
| **Sa.HybridFileStorage.Postgres** | PostgreSQL provider implementation |
| **Sa.Media** | Async, memory-efficient WAV file reader (`AsyncWavReader`) |
| **Sa.Media.FFmpeg** | FFmpeg .NET wrapper with built-in binaries (Win x64 / Linux), audio conversion, metadata extraction, channel split/join, DI support |
| **Sa.Outbox** | Base Outbox pattern infrastructure for reliable message publishing |
| **Sa.Outbox.PostgreSql** | PostgreSQL Outbox implementation — parallel processing, tenant support, scheduled data cleanup |
| **Sa.Partitional.PostgreSql** | Declarative PostgreSQL table partitioning (time: day/month/year; list; range) with migration/deletion schedules |
| **Sa.Schedule** | Scheduled task executor with failure strategies (close app, stop job, stop all jobs, ignore) |
| **Sa.Utils.WorkQueue** | Async queue with concurrency limiting, built on `System.Threading.Channels` |

### Samples

Located in `src/Samples/`: Configuration.Web, FFMpeg.Console, HybridFileStorage.Console, Partitional.ConsoleApp, PgOutbox.ConsoleApp, Schedule.Console, Storage.Tests.

### Tests

Located in `src/Tests/`: 15 test projects using **xunit v3**, **Testcontainers** (PostgreSQL + Minio) for integration tests. Test fixtures in `src/Tests/Fixtures/`.

---

## Building and Running

### Prerequisites

- .NET 10 SDK
- PowerShell (for local build scripts)

### Build Commands

```powershell
# Full build (clean + restore + build)
.\build\do_build.ps1

# Run all tests
.\build\do_test.ps1

# Package NuGet packages (produces .nupkg + .snupkg in dist/)
.\build\do_package.ps1

# Push to local registry
.\build\do_push_local.ps1

# Push to prod (nuget.org)
.\build\do_push_prod.ps1
```

### Direct dotnet commands

```powershell
# Restore
dotnet restore src/Sa.slnx -c Release

# Build
dotnet build src/Sa.slnx -c Release -v n

# Test (all)
dotnet test src/Sa.slnx -v n

# Test CI (skip tests requiring local Docker infrastructure)
dotnet test src/Sa.slnx --filter "Category!=Local"
```

### GitHub Actions

Workflow in `.github/workflows/` — builds on `main` branch push/PR. Uses `dotnet 9.x` runtime in CI (despite targeting net10.0). Note: tests are commented out in CI.

---

## Architecture Notes

### Shared Code Pattern

Common utilities live in `src/Sa/` and are linked into consuming projects via MSBuild `<Compile Include="..." Link="AsLink\..." />`. This avoids duplication while keeping projects independently buildable. Linked classes include:

- `Classes/`: LockRenewer, MurmurHash3, Retry, ResetLazy, Section, MimeTypeMap, IArrayPool, LockRenewer
- `Extensions/`: DateTimeExtensions, EnumerableExtensions, ExceptionExtensions, SpanExtensions, StringExtensions, NumericExtensions, StrToExtensions, GuidExtensions

### Project Dependencies

```
Sa.Utils.WorkQueue → (none)
Sa.Schedule        → Sa.Utils.WorkQueue
Sa.Outbox          → Sa.Schedule
Sa.Partitional.PostgreSql → Sa.Schedule + Sa.Data.PostgreSql
Sa.Outbox.PostgreSql    → Sa.Outbox + Sa.Partitional.PostgreSql (+ object pool, recycler mem stream)
Sa.Data.S3               → (none, just Npgsql indirectly)
Sa.HybridFileStorage     → (base abstraction)
Sa.HybridFileStorage.S3  → Sa.Data.S3 + Sa.HybridFileStorage
Sa.Configuration         → Microsoft.Extensions.Hosting
Sa.Configuration.PostgreSql → (standalone)
```

### Common Properties (inherited by all packages)

From `Common.Properties.xml`:
- Target: `net10.0`
- AOT: `PublishAot=true`, `IsAotCompatible=true`
- Nullable: enabled
- Analyzers: enabled
- TrimmerSingleWarn: false
- Symbols: included
- License: MIT

From `Common.NuGet.Properties.xml`: additional shared package references (logging, DI, SourceLink).

### Testing Conventions

- Framework: **xunit v3** (not classic xunit)
- Integration tests use **Testcontainers** (PostgreSQL + Minio)
- Test projects import `Host.Test.Properties.xml` for common test config
- Local-dependent tests are tagged `Category!=Local` for CI

---

## Development Conventions

- **ImplicitUsings** and **Nullable** enabled across all projects
- **Central Package Management** — all versions in `Directory.Packages.props`
- **SourceLink** enabled for debug symbol linking to GitHub
- **InternalsVisibleTo** used for test project access to internal members
- No `_editorconfig` rules beyond standard .NET conventions
- All projects use SDK-style csproj format
- Solution managed via `.slnx` (new solution format)
