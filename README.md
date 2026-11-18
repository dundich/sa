# Sa — .NET 10 Infrastructure Libraries

**English** | [Русский](README-ru.md)

Reusable infrastructure libraries for distributed .NET 10 systems — **Native AOT compatible**, **nullable enabled**, built on modern .NET primitives.

> ⚠️ **Stability.** All packages are still on major-version `0` (`0.x.y`), so public APIs may change in a breaking way between releases without notice — use at your own risk.

---

## Contents

- [Libraries](#libraries)
  - [Sa.Configuration — CLI Arguments & Secrets](#saconfiguration--cli-arguments--secrets)
  - [Sa.Configuration.PostgreSql — Dynamic DB Configuration](#saconfigurationpostgresql--dynamic-db-configuration)
  - [Sa.Data.PostgreSql — Npgsql Data Client](#sadatapostgresql--npgsql-data-client)
  - [Sa.Data.S3 — S3 Data Client](#sadatas3--s3-data-client)
  - [Sa.Data.TempFolder — Temporary Folder Manager](#sadatatempfolder--temporary-folder-manager)
  - [Sa.Outbox — Transactional Outbox Core](#saoutbox--transactional-outbox-core)
  - [Sa.Outbox.PostgreSql — PostgreSQL Provider](#saoutboxpostgresql--postgresql-provider)
  - [Sa.Outbox.Redis — Redis Provider](#saoutboxredis--redis-provider)
  - [Sa.Partitional.PostgreSql — Declarative Partitioning](#sapartitionalpostgresql--declarative-partitioning)
  - [Sa.Schedule — Scheduled Task Executor](#saschedule--scheduled-task-executor)
  - [Sa.HybridFileStorage — Multi-Provider File Storage](#sahybridfilestorage--multi-provider-file-storage)
  - [Sa.Media — Async WAV Reader](#samedia--async-wav-reader)
  - [Sa.Media.FFmpeg — FFmpeg .NET Wrapper](#samediaffmpeg--ffmpeg-net-wrapper)
  - [Sa.Utils.WorkQueue — Async Queue with Concurrency Limiting](#sautilsworkqueue--async-queue-with-concurrency-limiting)
- [Samples](#samples)
- [Tests](#tests)
- [Building](#building)
- [Contributing](#contributing)
- [Architecture](#architecture)
- [License](#license)

## Libraries

### [Sa.Configuration](src/Libraries/Configuration/Sa.Configuration) — CLI Arguments & Secrets

`Arguments` is a dictionary-like command-line argument parser with typed getters (`GetBool`, `GetInt`, `GetTimeSpan`, etc.). `Secrets` provides secure management of secrets from files, environment variables, and host-key files, with chained stores and `${secret:key}` templating.

---

### [Sa.Configuration.PostgreSql](src/Libraries/Configuration/Sa.Configuration.PostgreSql) — Dynamic DB Configuration

Adds a PostgreSQL-backed `IConfigurationSource` so configuration changes in the database take effect in-app without redeploy.

---


### [Sa.Data.PostgreSql](src/Libraries/Data/Sa.Data.PostgreSql) — Npgsql Data Client

Lightweight Npgsql wrapper: `IPgDataSource` with keyed DI registration, `ExecuteNonQuery`/`ExecuteScalar`/`ExecuteReader` helpers, typed row mapping, transactions, and SQL command extensions — no ORM, Native AOT friendly.

---

### [Sa.Data.S3](src/Libraries/Data/Sa.Data.S3) — S3 Data Client

A Minio-compatible S3 client for data operations.

---

### [Sa.Data.TempFolder](src/Libraries/Data/Sa.Data.TempFolder) — Temporary Folder Manager

`AddSaTempFolder` registers a keyed `ITempFolder` (a root directory plus strategies): path-guarded subfolders, debounced activity marking, age-based background cleanup with level-by-level descent, volume tracking and free-space watching with edge-triggered events, read-only mode, and keyed multi-instance registration.

---

### [Sa.Outbox](src/Libraries/Outbox/Sa.Outbox) — Transactional Outbox Core

Provider-agnostic core of the **Transactional Outbox** pattern: publication and consumer contracts, tenant/metadata registration, batching, retry policy and delivery status handling. Storage and delivery mechanics come from a provider package.

Providers: [Sa.Outbox.PostgreSql](src/Libraries/Outbox/Sa.Outbox.PostgreSql) — ✅ production-ready, [Sa.Outbox.Redis](src/Libraries/Outbox/Sa.Outbox.Redis) — 🧪 packaged, API may still change, in-memory — 🧪 built into `Sa.Outbox` as a reference/dev-test provider.

---

### [Sa.Outbox.PostgreSql](src/Libraries/Outbox/Sa.Outbox.PostgreSql) — PostgreSQL Provider

A PostgreSQL implementation of the **Transactional Outbox** pattern. Messages are recorded in an outbox table alongside business operations in a single transaction, then delivered with retries and status tracking. Consumption is concurrent (`SKIP LOCKED`); offset coordination uses advisory locks. Use at your own risk.

---

### [Sa.Outbox.Redis](src/Libraries/Outbox/Sa.Outbox.Redis) — Redis Provider

A Redis Streams implementation of the same outbox contract: the log, consumer cursors and retry state live in streams and the consumer group's pending entries list (PEL), with no Lua and single-key commands only, so it works on Redis Cluster as is.

---

### [Sa.Partitional.PostgreSql](src/Libraries/Misc/Sa.Partitional.PostgreSql) — Declarative Partitioning

Declarative PostgreSQL table partitioning (range by day/month/year and list) with a fluent builder, automated migration of future partitions, retention-based cleanup, and an in-memory cache that auto-invalidates on runtime changes.

---

### [Sa.Schedule](src/Libraries/Misc/Sa.Schedule) — Scheduled Task Executor

Configurable scheduled tasks supporting cron expressions, fixed intervals, and one-shot delays, with per-job failure strategies, retries, concurrency limits, interceptors, and runtime start/stop/restart. ([Cron docs EN](src/Libraries/Misc/Sa.Schedule/Cron/CRON.md), [Cron docs RU](src/Libraries/Misc/Sa.Schedule/Cron/CRON-ru.md))

---

### [Sa.HybridFileStorage](src/Libraries/Hybrid/Sa.HybridFileStorage) — Multi-Provider File Storage

`IHybridFileStorage` abstracts upload/download/delete across multiple storage providers (FileSystem, S3, PostgreSQL) with automatic sequential failover, batch operations, and per-provider interceptors. Providers validate their options eagerly at registration, so misconfiguration fails early instead of on every operation.

Providers: [`Sa.HybridFileStorage.FileSystem`](src/Libraries/Hybrid/Sa.HybridFileStorage.FileSystem), [`Sa.HybridFileStorage.S3`](src/Libraries/Hybrid/Sa.HybridFileStorage.S3), [`Sa.HybridFileStorage.Postgres`](src/Libraries/Hybrid/Sa.HybridFileStorage.Postgres).

---

### [Sa.Media](src/Libraries/Media/Sa.Media) — Async WAV Reader

A memory-efficient, fully async WAV reader built on `System.IO.Pipelines` — parses the WAV header, reads raw or normalized double samples per channel, converts between PCM16/24/32 and IEEE float, and produces streaming chunks with a configurable batch size.

---

### [Sa.Media.FFmpeg](src/Libraries/Media/Sa.Media.FFmpeg) — FFmpeg .NET Wrapper

A cross-platform FFmpeg wrapper with bundled fully-static binaries (win-x64, linux-x64) that work out of the box — no system install. Covers metadata extraction, audio conversion (PCM S16/S32 LE, F32 LE, raw, MP3, OGG), channel split/join, streaming I/O via streams, and DI.

---

### [Sa.Utils.WorkQueue](src/Libraries/Misc/Sa.Utils.WorkQueue) — Async Queue with Concurrency Limiting

A high-performance async task queue on `System.Threading.Channels` with bounded capacity and back-pressure (enqueue strategies on a full buffer: `Wait`/`Skip`/`Throw`), runtime-adjustable concurrency (`ConcurrencyLimit`/`MaxConcurrency`) with dynamic scaling, reader cancellation order (`Lifo`•`Fifo`•`RoundRobin`•`Random`) and cancel modes (`Hard`/`Soft`), DI, idempotent shutdown, error strategies, and status callbacks.

---

## Samples

Located in `src/Samples/`:

| Sample | Description |
|--------|-------------|
| [Configuration.Web](src/Samples/Configuration.Web) | CLI args + secrets in ASP.NET |
| [FFMpeg.Console](src/Samples/FFMpeg.Console) | FFmpeg metadata extraction |
| [HybridFileStorage.Console](src/Samples/HybridFileStorage.Console) | Multi-provider file storage |
| [InMemoryOutbox.ConsoleApp](src/Samples/InMemoryOutbox.ConsoleApp) | Outbox with no infrastructure (in-memory provider) |
| [Partitional.ConsoleApp](src/Samples/Partitional.ConsoleApp) | Declarative partitioning |
| [PgOutbox.ConsoleApp](src/Samples/PgOutbox.ConsoleApp) | Outbox pattern demo |
| [Schedule.Console](src/Samples/Schedule.Console) | Scheduled task executor |
| [Schedule.Configuration.Console](src/Samples/Schedule.Configuration.Console) | Scheduled jobs re-timed/disabled from appsettings.json |
| [Echo](src/Samples/Echo) | Audio channel separation on Sa.Media |
| [WorkQueue.Console](src/Samples/WorkQueue.Console) | Sa.WorkQueue demo with runtime concurrency changes |

---

## Tests

Located in `src/Tests/`: 17 test projects using **xunit v3** and **Testcontainers** (PostgreSQL + SeaweedFS) for integration tests.

---

## Building

```bash
# Full build
./build-sh/do-build.sh
# Run tests
./build-sh/do-test.sh
# Package NuGet packages
./build-sh/do-package.sh
```

Direct dotnet commands:

```powershell
dotnet restore src/Sa.slnx
dotnet build src/Sa.slnx -c Release
dotnet test --solution src/Sa.slnx -c Release --no-build --no-restore
```

---

## Contributing

[`AGENTS.md`](AGENTS.md) holds the build, test and packaging constraints; the domain glossary is
[`GLOSSARY.md`](GLOSSARY.md).

---

## Architecture

- Targets **.NET 10.0** with **Native AOT**
- Uses **Central Package Management** (`Directory.Packages.props`)
- Shared utilities in **Sa** are linked into consuming projects
- All packages use SDK-style csproj with implicit usings, nullable, and analyzers
- Solution managed via `.slnx`
- Tests use **xunit v3** and **Testcontainers** (PostgreSQL + SeaweedFS), isolated per test class

## License

MIT
