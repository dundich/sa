# Sa — infrastructure libraries for .NET 10

**English** | [Русский](README-ru.md)

Reusable building blocks for distributed .NET services: configuration, data access, transactional outbox, file storage, media, scheduling. **Native AOT compatible**, nullable enabled, no ORM.

> ⚠️ **Pre-1.0.** Every package is still on `0.x.y`, so its public API may change between releases without notice.

Each library name below links to its folder, and every folder has a `Readme.md` with the API, DI registration, options and examples. The one-liners here only say *what problem a library solves*.

## Contents

- [Configuration](#configuration)
- [Data](#data)
- [Outbox](#outbox)
- [File storage](#file-storage)
- [Media](#media)
- [Scheduling & concurrency](#scheduling--concurrency)
- [Samples](#samples)
- [Building & testing](#building--testing)
- [Deep dives](#deep-dives)
- [License](#license)

## Configuration

- **[Sa.Configuration](src/Libraries/Configuration/Sa.Configuration)** — typed access to command-line arguments, and to secrets kept in files, environment variables or host-key files.
- **[Sa.Configuration.PostgreSql](src/Libraries/Configuration/Sa.Configuration.PostgreSql)** — keeps configuration in a PostgreSQL table, so a change applies at runtime without a redeploy.

## Data

- **[Sa.Data.PostgreSql](src/Libraries/Data/Sa.Data.PostgreSql)** — a thin Npgsql wrapper: keyed data sources, query helpers, typed row mapping, transactions; no ORM.
- **[Sa.Data.S3](src/Libraries/Data/Sa.Data.S3)** — an S3-compatible client for bucket and object operations.
- **[Sa.Data.TempFolder](src/Libraries/Data/Sa.Data.TempFolder)** — one controlled temp directory per purpose: safe subfolders, cleanup by age, free-space monitoring.
- **[Sa.Partitional.PostgreSql](src/Libraries/Misc/Sa.Partitional.PostgreSql)** — declarative table partitioning: partitions are created ahead of time, old data is dropped by retention policy.

## Outbox

The [transactional outbox](https://microservices.io/patterns/data/transactional-outbox.html) pattern: a message is written in the same transaction as the business change, then delivered with retries.

- **[Sa.Outbox](src/Libraries/Outbox/Sa.Outbox)** — provider-agnostic core: contracts, batching, retries, delivery status. Ships an in-memory provider for development and tests.
- **[Sa.Outbox.PostgreSql](src/Libraries/Outbox/Sa.Outbox.PostgreSql)** — the production-ready PostgreSQL provider.
- **[Sa.Outbox.Redis](src/Libraries/Outbox/Sa.Outbox.Redis)** — a Redis provider: packaged and usable, but its API may still change.

## File storage

- **[Sa.HybridFileStorage](src/Libraries/Hybrid/Sa.HybridFileStorage)** — one upload/download/delete interface over several backends, with automatic sequential failover between them.
  - Providers: **[FileSystem](src/Libraries/Hybrid/Sa.HybridFileStorage.FileSystem)**, **[S3](src/Libraries/Hybrid/Sa.HybridFileStorage.S3)**, **[PostgreSQL](src/Libraries/Hybrid/Sa.HybridFileStorage.Postgres)**.

## Media

- **[Sa.Media](src/Libraries/Media/Sa.Media)** — a memory-efficient, fully async WAV reader: header parsing, per-channel samples, PCM16/24/32 and IEEE float.
- **[Sa.Media.FFmpeg](src/Libraries/Media/Sa.Media.FFmpeg)** — an FFmpeg wrapper with bundled static binaries (win-x64, linux-x64), so there is nothing to install: metadata, audio conversion, channel split/join, stream I/O.

## Scheduling & concurrency

- **[Sa.Schedule](src/Libraries/Misc/Sa.Schedule)** — runs tasks on cron expressions, fixed intervals or one-shot delays, with retries, concurrency limits and start/stop at runtime.
- **[Sa.Utils.WorkQueue](src/Libraries/Misc/Sa.Utils.WorkQueue)** — a bounded async work queue with back-pressure and a concurrency limit that can be changed at runtime.

## Samples

Ten runnable examples — one per library, each with its run command and expected output — live in [`src/Samples/`](src/Samples/README.md).

## Building & testing

Requires the **.NET 10 SDK**; integration tests also need a running **Docker**.

```bash
./build-sh/do-build.sh     # clean, restore, build
./build-sh/do-test.sh      # build and run every test
./build-sh/do-package.sh   # produce the NuGet packages
```

Plain `dotnet` works too: `dotnet build src/Sa.slnx -c Release`, then `dotnet test --solution src/Sa.slnx -c Release --no-build --no-restore`.

Note for contributors: the suites start real PostgreSQL and SeaweedFS containers, so how they are built, filtered and throttled is covered in [`AGENTS.md`](AGENTS.md).

## Deep dives

This README stays a map; the details live next to the code.

| Topic | Where |
|-------|-------|
| API, DI registration, options, examples | `Readme.md` in each library folder |
| Cron syntax | [English](src/Libraries/Misc/Sa.Schedule/Cron/CRON.md) · [Русский](src/Libraries/Misc/Sa.Schedule/Cron/CRON-ru.md) |
| Shared `Sa` source (linked into packages, not published) | [src/Libraries/Sa/Readme.md](src/Libraries/Sa/Readme.md) |
| Project layout, build, test and packaging rules | [AGENTS.md](AGENTS.md) |
| Domain vocabulary | [GLOSSARY.md](GLOSSARY.md) |

## License

MIT
