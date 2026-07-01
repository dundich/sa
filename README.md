# Sa — .NET 10 Infrastructure Libraries

Reusable infrastructure libraries for distributed .NET 10 systems — **Native AOT compatible**, **nullable enabled**, built on modern .NET primitives.

---

## Libraries

### [Sa](src/Sa) — Shared Utilities

Common building blocks consumed by other packages via `<Compile Include="..." Link="..."/>`:

- `LockRenewer` — automatic lock extension with configurable renewal interval
- `MurmurHash3` — compact hash for type identification and partitioning
- `Retry` — retry helpers with exponential backoff
- `ResetLazy<T>` — lazily-evaluated, resettable cached value
- Extension methods: `DateTimeExtensions`, `EnumerableExtensions`, `ExceptionExtensions`, `SpanExtensions`, `StringExtensions`, `NumericExtensions`, `StrToExtensions`, `GuidExtensions`

---

### [Sa.Configuration](src/Sa.Configuration) — CLI Arguments & Secrets

| Type | Purpose |
|------|---------|
| `Arguments` | Command-line argument parser — dictionary-like access, typed getters (`GetBool`, `GetInt`, `GetTimeSpan`, etc.) |
| `Secrets` | Secure secrets management from files, environment variables, and host-key files. Supports chained stores and templating (`${secret:key}`) |

```csharp
// Arguments
var args = new Arguments(argsArray);
var port = args.GetInt("port") ?? 8080;

// Secrets
var secrets = Secrets.CreateDefault();
var populated = secrets.PopulateSecrets("Host={db_host};Password=${db_password}");
```

---

### [Sa.Configuration.PostgreSql](src/Sa.Configuration.PostgreSql) — Dynamic DB Configuration

PostgreSQL-backed `IConfigurationSource` — changes in the database reflect in-app without redeploy.

```csharp
builder.Configuration.AddSaPostgreSqlConfiguration(new PostgreSqlConfigurationOptions(
    connectionString: "Host=localhost;Database=myapp",
    selectSql: "SELECT key, value FROM app_config",
    parameters: Array.Empty<NpgsqlParameter>()));
```

Supports parameterised queries and `PgRetryStrategy` for transient error handling.

---

### [Sa.Data.PostgreSql](src/Sa.Data.PostgreSql) — Lightweight Npgsql Wrapper

Thin wrapper over Npgsql for typical database operations — zero ORM overhead, Native AOT friendly.

| Method | Description |
|--------|-------------|
| `ExecuteNonQueryAsync` | INSERT / UPDATE / DELETE / DDL with row count return |
| `ExecuteScalarAsync` / `ExecuteScalarTypedAsync<T>` | Single value with auto-cast |
| `ExecuteReaderAsync` | Streaming row reading via callback (no full result in memory) |
| `ExecuteReaderListAsync<T>` | Collect all rows into `List<T>` |
| `ExecuteReaderFirstAsync<T>` | First column of first row (Guid, TimeSpan, DateTime, int, long, etc.) |
| `ExecuteReaderSingleAsync<T>` / `TryExecuteReaderSingleAsync<T>` | Safe scalar with nullability |
| `ExecuteTransactionAsync` | Atomic operations with auto commit/rollback |
| `BeginBinaryImportAsync` | Fast COPY BINARY for bulk inserts |
| `PgRetryStrategy` | Retry with jitter for transient Npgsql errors |

DI registration: `AddSaPostgreSqlDataSource()`.

---

### [Sa.Data.S3](src/Sa.Data.S3) — S3 Data Client

Minio-compatible S3 client for data operations.

---

### [Sa.Outbox](src/Sa.Outbox) — Transactional Outbox Core

Base infrastructure for the **Transactional Outbox** pattern — guarantees atomic message recording alongside business operations within a single database transaction, with reliable delivery, retries, blocking, multi-threading, and multi-tenancy support.

Defines abstractions; concrete DB work (PostgreSQL, SQL Server, etc.) is implemented by providers (`Sa.Outbox.PostgreSql`, `Sa.Outbox.SqlServer`).

| Key Type | Purpose |
|----------|---------|
| `IOutboxBuilder` | Fluent configuration builder |
| `IOutboxMessagePublisher` | Publish messages to outbox |
| `IConsumer<TMessage>` | Message consumer interface |
| `IOutboxContextOperations<T>` | Delivery status change operations (`Ok`, `Error`, `Warn`, `Postpone`, etc.) |
| `OutboxConsumerSettings` | Immutable snapshot of consumer group settings (interval, batches, concurrency, retries…) |
| `OutboxConsumerSettingsBuilder` | Fluent builder for creating/updating `OutboxConsumerSettings` |
| `IOutboxConsumerManager` | Runtime manager: atomic swap, pause/resume, change subscriptions |
| `IDeliverySnapshot` | Read-only view of registered deliveries for diagnostics |
| `DeliveryStatus` / `DeliveryStatusCode` | HTTP-like delivery status codes |
| `ExponentialBackoffRetryStrategy` | Exponential backoff with jitter |

See individual provider READMEs for full usage examples.

---

### [Sa.Outbox.PostgreSql](src/Sa.Outbox.PostgreSql) — PostgreSQL Provider

Production-ready PostgreSQL implementation with UUID v7 IDs, BINARY COPY bulk insertion, `SKIP LOCKED` concurrent consumption, advisory locks for offset coordination, and automated partition migration/cleanup.

See [full README](src/Sa.Outbox.PostgreSql/Readme.md).

---

### [Sa.Partitional.PostgreSql](src/Sa.Partitional.PostgreSql) — Declarative Partitioning

Declarative PostgreSQL table partitioning for .NET 10 — range (day/month/year) and list partitioning with automated migration, cleanup scheduling, and in-memory caching.

- **Range partitioning** by day, month, or year with automatic timestamp-based naming
- **List partitioning** by string or numeric keys with hierarchical child partitions
- **Fluent builder API** for declaring tables, tuning fillfactor, custom constraints, and migrations
- **Automated migration** — pre-create future partitions as a background job
- **Automated cleanup** — drop old partitions past a configurable retention window
- **In-memory cache** — avoids repeated catalog queries; auto-invalidates on runtime changes
- **StrOrNum** discriminated union for type-safe partition key values

See [Guide](src/Sa.Partitional.PostgreSql/Guide.md) and [API Reference](src/Sa.Partitional.PostgreSql/ApiReference.md).

---

### [Sa.Schedule](src/Sa.Schedule) — Scheduled Task Executor

Configurable and executable scheduled tasks with failure strategies.

| Feature | Description |
|---------|-------------|
| **Flexible timing** | Cron expressions, fixed intervals (seconds/minutes/hours/days), one-shot delays |
| **Failure strategies** | `CloseApplication`, `AbortJob`, `StopAllJobs`, or `Ignore` |
| **Retry on failure** | Configurable retry count per job |
| **Concurrency control** | Per-job `ConcurrencyLimit` and `MaxConcurrency` |
| **Interceptors** | `IJobInterceptor` for pre/post execution hooks |
| **Error handlers** | Global `Func<IJobContext, Exception, bool>` error handlers |
| **Runtime management** | Start, stop, restart individual schedulers via `IScheduler` |

```csharp
builder.Services.AddSaSchedule(builder => builder
    .UseHostedService()
    .AddJob<MyCleanupJob>()
        .WithName("cleanup")
        .EveryHours(1)
        .ConfigureErrorHandling(eh => eh.IfErrorRetry(3).ThenAbortJob()));
```

---

### [Sa.HybridFileStorage](src/Sa.HybridFileStorage) — Multi-Provider File Storage

`IHybridFileStorage` abstracts file operations across multiple storage providers (FileSystem, S3, PostgreSQL) with automatic failover.

| Capability | Description |
|------------|-------------|
| **Upload / Download / Delete** | Standard file operations with streaming |
| **Multi-provider** | Register any `IFileStorage` implementation |
| **Automatic failover** | Tries providers sequentially; aggregates errors if all fail |
| **Batch operations** | Parallel batch upload/download with progress reporting |
| **Interceptors** | Pre/post/on-error hooks per provider |
| **Built-in providers** | `InMemoryFileStorage` (testing), plus `FileSystem`, `S3`, `Postgres` in separate packages |

```csharp
builder.Services.AddSaHybridFileStorage(cfg => cfg
    .ConfigureStorage(sp => sp
        .AddStorage(new FileSystemStorage("/data/uploads"))
        .AddStorage(new S3Storage("s3-bucket"))));
```

Providers: [`Sa.HybridFileStorage.FileSystem`](src/Sa.HybridFileStorage.FileSystem), [`Sa.HybridFileStorage.S3`](src/Sa.HybridFileStorage.S3), [`Sa.HybridFileStorage.Postgres`](src/Sa.HybridFileStorage.Postgres).

---

### [Sa.Media](src/Sa.Media) — Async WAV Reader

Memory-efficient, fully async WAV file reader built on `System.IO.Pipelines`.

| Method | Description |
|--------|-------------|
| `CreateFromFile` / `Create(Stream)` | Factory methods |
| `GetHeaderAsync` | Parse WAV header |
| `ReadSamplesPerChannelAsync` | Raw bytes per channel |
| `ReadDoubleSamplesAsync` | Normalized double samples |
| `ConvertToFormatAsync` | Convert to PCM16/24/32, IEEE float |
| `ReadStreamableChunksAsync` | Streaming chunks with configurable batch size |

```csharp
using var reader = AsyncWavReader.CreateFromFile("audio.wav");
var header = await reader.GetHeaderAsync();
await foreach (var packet in reader.ReadDoubleSamplesAsync())
{
    Console.WriteLine($"Ch{packet.ChannelId}: {packet.Sample}");
}
```

---

### [Sa.Media.FFmpeg](src/Sa.Media.FFmpeg) — FFmpeg .NET Wrapper

Ready-to-use FFmpeg integration with built-in binaries (Windows x64 + Linux) and DI support.

| Interface | Purpose |
|-----------|---------|
| `IFFMpegExecutor` | Audio/video conversion (PCM16LE, MP3, OGG) |
| `IFFProbeExecutor` | Metadata extraction (duration, channels, sample rate, bitrate) |
| `IPcmS16LeChannelManipulator` | Channel split/join operations |
| `IFFMpegLocator` | Auto-discovery of FFmpeg executable |

```csharp
builder.Services.AddSaFFMpeg();

var probe = IFFProbeExecutor.Default;
var meta = await probe.GetMetaInfo("input.mp3");
Console.WriteLine($"Duration: {meta.Duration}s, Channels: {meta.Channels}");
```

---

### [Sa.Utils.WorkQueue](src/Sa.Utils.WorkQueue) — Async Queue with Concurrency Limiting

High-performance task queue built on `System.Threading.Channels` with bounded capacity, dynamic concurrency scaling, and multiple reader-scaling strategies.

| Feature | Description |
|---------|-------------|
| **Bounded queue** | Back-pressure via `BoundedChannel` — overflow handled by `Wait`, `DropWrite`, `DropOldest` |
| **Dynamic concurrency** | Change `ConcurrencyLimit` at runtime |
| **Scaling strategies** | `Lifo` • `Fifo` • `RoundRobin` • `Random` |
| **Error strategies** | `Continue`, `StopReader`, `ShutdownQueue` |
| **Status callbacks** | `Running` → `Completed` / `Faulted` / `Cancelled` / `Aborted` |
| **Zero-allocation logging** | `[LoggerMessage]` source generator |

See [full README](src/Sa.Utils.WorkQueue/Readme.md).

---

## Samples

Located in `src/Samples/`:

| Sample | Description |
|--------|-------------|
| [Configuration.Web](src/Samples/Configuration.Web) | CLI args + secrets in ASP.NET |
| [FFmpeg.Console](src/Samples/FFmpeg.Console) | FFmpeg metadata extraction |
| [HybridFileStorage.Console](src/Samples/HybridFileStorage.Console) | Multi-provider file storage |
| [Partitional.ConsoleApp](src/Samples/Partitional.ConsoleApp) | Declarative partitioning |
| [PgOutbox.ConsoleApp](src/Samples/PgOutbox.ConsoleApp) | Outbox pattern demo |
| [Schedule.Console](src/Samples/Schedule.Console) | Scheduled task executor |
| [Storage.Tests](src/Samples/Storage.Tests) | Hybrid file storage tests |

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

## License

MIT
