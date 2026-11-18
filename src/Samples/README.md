# Samples Quick Start

**English** | [Русский](README-ru.md)

Collection of runnable samples demonstrating each library in the **Sa** suite.
All samples target **.NET 10.0**, most are published as **Native AOT**, and they follow the same DI + Generic Host pattern.

> **Infrastructure:** only three samples need a database — `Configuration.Web`, `Partitional.ConsoleApp` and `PgOutbox.ConsoleApp` use PostgreSQL.
> Start it with `docker-compose up -d db` (see [`docker-compose.yml`](./docker-compose.yml)).

---

## Table of Contents

| # | Sample | Library | Type | Description |
|---|--------|---------|------|-------------|
| 1 | [Configuration.Web](#1-configurationweb) | `Sa.Configuration` + `Sa.Configuration.PostgreSql` | Web API | Dynamic config from CLI args + PostgreSQL table |
| 2 | [FFMpeg.Console](#2-ffmpegconsole) | `Sa.Media.FFmpeg` | Console | Version check, codec list, MP3→WAV conversion |
| 3 | [HybridFileStorage.Console](#3-hybridfilestorageconsole) | `Sa.HybridFileStorage` | Console | Hybrid file storage with provider abstraction |
| 4 | [Partitional.ConsoleApp](#4-partitionalconsoleapp) | `Sa.Partitional.PostgreSql` | Console | Declarative table partitioning with migration schedule |
| 5 | [PgOutbox.ConsoleApp](#5-pgoutboxconsoleapp) | `Sa.Outbox.PostgreSql` | Console | Reliable message publishing via Outbox pattern |
| 6 | [Schedule.Console](#6-scheduleconsole) | `Sa.Schedule` | Console | Scheduled job executor with failure strategies |
| 7 | [WorkQueue.Console](#7-workqueueconsole) | `Sa.Utils.WorkQueue` | Console | Bounded queue with runtime concurrency scaling and per-item error strategies |
| 8 | [InMemoryOutbox.ConsoleApp](#8-inmemoryoutboxconsoleapp) | `Sa.Outbox` (in-memory) | Console | Outbox with no database, no Docker and no connection string |
| 9 | [Schedule.Configuration.Console](#9-scheduleconfigurationconsole) | `Sa.Schedule` | Console | Jobs re-timed and disabled from appsettings.json |
| 10 | [Echo](#10-echo) | `Sa.Media` | Console | Cross-feed channel separation of a stereo WAV |

---

## 1. Configuration.Web

Demonstrates reading configuration from command-line arguments and a PostgreSQL `settings` table, served as a minimal ASP.NET Core API.

### What it does

1. Creates a slim ASP.NET app.
2. Parses CLI args via `Sa.Configuration.Arguments`.
3. Reads secrets (e.g. connection strings) from env vars / files.
4. Seeds a `settings` table in PostgreSQL.
5. Hot-reloads settings from DB — changes reflect without restart.
6. Exposes `GET /settings` returning all config keys.

### Run

```powershell
# 1. Ensure PostgreSQL is running
docker-compose up -d db

# 2. Set the connection string (or pass as CLI arg)
$env:sa__pg__connection = "Host=localhost;Username=postgres;Password=postgres;Database=postgres"

# 3. Run
dotnet run --project Configuration.Web
```

### Test

```powershell
curl http://localhost:5000/settings
```

Expected response:

```json
[
  { "key": "sa:pg:connection", "value": "Host=localhost;..." },
  { "key": "theme",       "value": "dark" },
  { "key": "language",    "value": "en" },
  { "key": "notifications","value": "enabled" },
  { "key": "secret",      "value": null }
]
```

---

## 2. FFMpeg.Console

Demonstrates using `Sa.Media.FFmpeg` for audio processing with built-in FFmpeg binaries.

### What it does

1. Gets FFmpeg version.
2. Lists available codecs.
3. Converts `data/input.mp3` → `data/output.wav` (mono PCM_S16LE).

### Run

```powershell
dotnet run --project FFMpeg.Console
```

### Expected output

```
Hello, [Sa.Media.FFmpeg]!
ffmpeg version 6.x...
[aac, ac3, flac, ..., pcm_s16le, ...]
```

---

## 3. HybridFileStorage.Console

Demonstrates `Sa.HybridFileStorage` — an abstracted file storage layer with automatic provider failover.

### What it does

1. Registers `InMemoryFileStorage` as the primary provider.
2. Uploads a text file (`"Hello, HybridFileStorage!"`).
3. Downloads it back and verifies content.

### Run

```powershell
dotnet run --project HybridFileStorage.Console
```

### Expected output

```
starting
completed:Hello, HybridFileStorage!
```

---

## 4. Partitional.ConsoleApp

Demonstrates declarative PostgreSQL table partitioning with scheduled migrations and cleanup.

### What it does

1. Configures a `customer` table partitioned by list (`country`, `city`).
2. Defines migration schedules for RU (Moscow, Samara), USA (Alabama, New York), FR (Paris, Lyon, Bordeaux).
3. Runs `partition.Migrate()` to create physical partitions.
4. Lists created partitions for the next 3 days.

### Run

```powershell
# 1. Ensure PostgreSQL is running
docker-compose up -d db

# 2. Run (uses hardcoded conn string)
dotnet run --project Partitional.ConsoleApp
```

### Expected output

```
Hello, Partitional.PostgreSql!
list of parts:
customer_20260701
customer_RU_Moscow_20260701
...
Successfully: True
```

---

## 5. PgOutbox.ConsoleApp

Demonstrates the Outbox pattern for reliable message publishing with PostgreSQL-backed outbox storage.

### What it does

1. Registers two consumer groups: `Group1Consumer` (every 5s, single iteration) and `RndConsumer` (every 25s, max 2 attempts).
2. Publishes 3 initial messages for tenant 1.
3. Background service continuously publishes random messages for tenants 1–3.
4. Consumers handle messages with various outcomes: Ok, Retry, Postpone, Warn, Abort, Error.

### Run

```powershell
# 1. Ensure PostgreSQL is running
docker-compose up -d db

# 2. Run
dotnet run --project PgOutbox.ConsoleApp
```

### Expected output

```
Hello, Pg Outbox!
======= Group1Consumer : 1 =======
2026-07-01T... #123: Hi 1 [Ok]
2026-07-01T... #124: Hi 2 [Ok]
2026-07-01T... #125: Hi 3 [Ok]
======= RndConsumer : 1 =======
...
```

---

## 6. Schedule.Console

Demonstrates `Sa.Schedule` — a scheduled job executor with failure handling strategies.

### What it does

1. Prompts whether to run as a hosted service (Y/n).
2. Registers `SomeJob` that runs every 2 seconds with retry-on-error logic.
3. Adds an interceptor that logs `<beg>` / `<end>` around each execution.
4. After 5s stops the scheduler, waits 2s, then restarts.
5. After 30s cancels everything.

### Run

```powershell
# Interactive: press 'n' for standalone mode or 'y' for hosted service
dotnet run --project Schedule.Console
```

### Standalone mode expected output

```
Hello, Schedule! As host service (Y/n): n

<beg>
2026-07-01T... 0: Some 2
<end>
<beg>
2026-07-01T... 1: Some 2
<end>
err 0
err 1
*** stopped & start after 2 sec
<beg>
2026-07-01T... 0: Some 2
<end>
*** cancelled on timeout
*** THE END ***
```

---

## 7. WorkQueue.Console

Demonstrates `Sa.Utils.WorkQueue` — a bounded, back-pressured task queue with runtime concurrency scaling.

### What it does

1. Creates a `SaWorkChannel<int>` with an initial concurrency limit of 3 and a max of 8.
2. Enqueues a batch of 10 items.
3. While items flow, scales concurrency up to 6 and back down to 2 on the fly.
4. Waits for idle, reports the processed count.
5. Gracefully shuts down — in-flight items finish, remaining items are reported as `Faulted`.

### Run

```powershell
dotnet run --project WorkQueue.Console
```

### Expected output

```
=== Sa.WorkQueue sample ===
Enqueued 10 items, concurrency = 3
[8] #1 started
[8] #1 -> Running
...
Scaled up -> concurrency = 6
Scaled down -> concurrency = 2
Processed 10 items, queue is idle: True
Enabled after shutdown: False
*** THE END ***
```

---

## 8. InMemoryOutbox.ConsoleApp

Demonstrates the same publish → consume pipeline as `PgOutbox.ConsoleApp`, but the outbox lives in process memory — no database, no Docker, no connection string.

### What it does

1. Registers the in-memory provider with `AddSaOutboxUsingInMemory(...)`.
2. Publishes 4 messages across two tenants.
3. A `PrintConsumer` receives them in batches and acknowledges each message with `Ok()`.
4. Waits for all 4 acknowledgements, then shuts the host down.

The consumers do not know which provider is behind them: swapping `AddSaOutboxUsingInMemory` for `AddSaOutboxUsingPostgreSql` is the only change needed for the durable version.

### Run

```powershell
dotnet run --project InMemoryOutbox.ConsoleApp
```

### Expected output

```
In-memory Outbox — no database, no Docker.
info: InMemoryOutbox.PrintConsumer[1]
      [print] tenant 1 #1: 01 — Hi from tenant 1
      ...
All messages delivered. The store lives in process memory only — restart loses it.
```

---

## 9. Schedule.Configuration.Console

Demonstrates `Sa.Schedule` where the jobs are declared in code and then re-timed or disabled from `appsettings.json` — configuration wins over code, no rebuild needed.

### What it does

1. Declares three jobs in code with defaults: `Heartbeat` every 30s, `SlowReport` every 2s, `NightlyCleanup` every 15min.
2. Calls `FromConfiguration("Schedule")`, so the `Schedule:Jobs` section overrides them:
   - `Heartbeat` → every 2s and fires at startup (`Immediate`);
   - `SlowReport` → `Disabled`;
   - `NightlyCleanup` → cron `0 3 * * *`.
3. Prints the bound options, then runs for 12 seconds so the override is visible.

### Run

```powershell
dotnet run --project Schedule.Configuration.Console
```

### Expected output

```
appsettings.json (Schedule:Jobs) — the code defaults above are overridden:
  Heartbeat        Immediate=True  Every=00:00:02
  SlowReport       Disabled=True
  NightlyCleanup   Cron=0 3 * * *
```

---

## 10. Echo

Demonstrates cross-feed channel separation on `Sa.Media`: a stereo recording is processed so that content shared by both channels is suppressed — a simple "remove the vocal" effect.

### What it does

1. Reads a stereo WAV (path from the first argument).
2. Runs `CrossFeedSeparator` with `AudioSeparationOptions`.
3. Writes the result to a temporary `.wav` file, prints its size, then deletes it.

### Run

```powershell
# From the repository root; without an argument the sample uses
# src/Tests/Sa.MediaTests/data/pсmS16Le.wav
dotnet run --project src/Samples/Echo -- <path-to-stereo-wav>
```

The default path is resolved relative to the repository root, so run this one from there rather than from `src/Samples`.

### Expected output

```
Wrote suppressed output: <random>.wav
Output size: 96077 bytes
Result deleted.
```

---

## Stopping Infrastructure

```powershell
docker-compose down
```
