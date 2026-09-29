# Sa — .NET 10 Infrastructure Libraries

Reusable infrastructure libraries for distributed .NET 10 systems — **Native AOT compatible**, **nullable enabled**, built on modern .NET primitives.

---

## Libraries

### [Sa.Configuration](src/Sa.Configuration) — CLI Arguments & Secrets

`Arguments` is a dictionary-like command-line argument parser with typed getters (`GetBool`, `GetInt`, `GetTimeSpan`, etc.). `Secrets` provides secure management of secrets from files, environment variables, and host-key files, with chained stores and `${secret:key}` templating.

---

### [Sa.Configuration.PostgreSql](src/Sa.Configuration.PostgreSql) — Dynamic DB Configuration

Adds a PostgreSQL-backed `IConfigurationSource` so configuration changes in the database take effect in-app without redeploy.

---

### [Sa.Data.PostgreSql](src/Sa.Data.PostgreSql) — Lightweight Npgsql Wrapper

A thin, Native AOT-friendly wrapper over Npgsql for typical database operations with no ORM overhead — non-query execution, scalars, streaming readers, transactions, binary COPY import, and jittered retries via `PgRetryStrategy`.

---

### [Sa.Data.S3](src/Sa.Data.S3) — S3 Data Client

A Minio-compatible S3 client for data operations.

---

### [Sa.Outbox.PostgreSql](src/Sa.Outbox.PostgreSql) — PostgreSQL Provider

A PostgreSQL implementation of the **Transactional Outbox** pattern. Messages are recorded in an outbox table alongside business operations in a single transaction, then delivered with retries and status tracking. Consumption is concurrent (`SKIP LOCKED`); offset coordination uses advisory locks. Use at your own risk.

---

### [Sa.Partitional.PostgreSql](src/Sa.Partitional.PostgreSql) — Declarative Partitioning

Declarative PostgreSQL table partitioning (range by day/month/year and list) with a fluent builder, automated migration of future partitions, retention-based cleanup, and an in-memory cache that auto-invalidates on runtime changes.

---

### [Sa.Schedule](src/Sa.Schedule) — Scheduled Task Executor

Configurable scheduled tasks supporting cron expressions, fixed intervals, and one-shot delays, with per-job failure strategies, retries, concurrency limits, interceptors, and runtime start/stop/restart.

---

### [Sa.HybridFileStorage](src/Sa.HybridFileStorage) — Multi-Provider File Storage

`IHybridFileStorage` abstracts upload/download/delete across multiple storage providers (FileSystem, S3, PostgreSQL) with automatic sequential failover, batch operations, and per-provider interceptors. Providers validate their options eagerly at registration, so misconfiguration fails early instead of on every operation.

Providers: [`Sa.HybridFileStorage.FileSystem`](src/Sa.HybridFileStorage.FileSystem), [`Sa.HybridFileStorage.S3`](src/Sa.HybridFileStorage.S3), [`Sa.HybridFileStorage.Postgres`](src/Sa.HybridFileStorage.Postgres).

---

### [Sa.Media](src/Sa.Media) — Async WAV Reader

A memory-efficient, fully async WAV reader built on `System.IO.Pipelines` — parses the WAV header, reads raw or normalized double samples per channel, converts between PCM16/24/32 and IEEE float, and produces streaming chunks with a configurable batch size.

---

### [Sa.Media.FFmpeg](src/Sa.Media.FFmpeg) — FFmpeg .NET Wrapper

A cross-platform FFmpeg wrapper with bundled fully-static binaries (win-x64, linux-x64) that work out of the box — no system install. Covers metadata extraction, audio conversion (PCM S16/S32 LE, F32 LE, raw, MP3, OGG), channel split/join, streaming I/O via streams, and DI.

---

### [Sa.Utils.WorkQueue](src/Sa.Utils.WorkQueue) — Async Queue with Concurrency Limiting

A high-performance async task queue on `System.Threading.Channels` with bounded capacity and back-pressure (enqueue strategies on a full buffer: `Wait`/`Skip`/`Throw`), runtime-adjustable concurrency (`ConcurrencyLimit`/`MaxConcurrency`) with dynamic scaling, reader cancellation order (`Lifo`•`Fifo`•`RoundRobin`•`Random`) and cancel modes (`Hard`/`Soft`), DI, idempotent shutdown, error strategies, and status callbacks.

---

## Samples

Located in `src/Samples/`:

| Sample | Description |
|--------|-------------|
| [Configuration.Web](src/Samples/Configuration.Web) | CLI args + secrets in ASP.NET |
| [FFMpeg.Console](src/Samples/FFMpeg.Console) | FFmpeg metadata extraction |
| [HybridFileStorage.Console](src/Samples/HybridFileStorage.Console) | Multi-provider file storage |
| [Partitational.ConsoleApp](src/Samples/Partitional.ConsoleApp) | Declarative partitioning |
| [PgOutbox.ConsoleApp](src/Samples/PgOutbox.ConsoleApp) | Outbox pattern demo |
| [Schedule.Console](src/Samples/Schedule.Console) | Scheduled task executor |

---

## Tests

Located in `src/Tests/`: 15 test projects using **xunit v3** and **Testcontainers** (PostgreSQL + Minio) for integration tests.

---

## Building

```powershell
# Full build
.\build\do_build.ps1

# Run tests
.\build\do_test.ps1

# Package NuGet packages
.\build\do_package.ps1
```

Direct dotnet commands:

```powershell
dotnet restore src/Sa.slnx -c Release
dotnet build src/Sa.slnx -c Release -v n
dotnet test src/Sa.slnx -v n
```

---

## Architecture

- Targets **.NET 10.0** with **Native AOT**
- Uses **Central Package Management** (`Directory.Packages.props`)
- Shared utilities in **Sa** are linked into consuming projects
- All packages use SDK-style csproj with implicit usings, nullable, and analyzers
- Solution managed via `.slnx`
- Tests use **xunit v3** and **Testcontainers** (PostgreSQL + Minio), isolated per test class

## License

MIT
