# Samples Quick Start

Collection of runnable samples demonstrating each library in the **Sa** suite.  
All samples target **.NET 10.0**, use **Native AOT**, and follow the same DI + Generic Host pattern.

> **Infrastructure:** most samples require PostgreSQL (and sometimes Minio).  
> Start shared infrastructure with: `docker-compose up -d` (see [`docker-compose.yml`](./docker-compose.yml)).

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

## Stopping Infrastructure

```powershell
docker-compose down
```
