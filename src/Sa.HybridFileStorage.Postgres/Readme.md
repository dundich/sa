# Sa.HybridFileStorage.Postgres

PostgreSQL-backed file storage provider for `Sa.HybridFileStorage`. Stores files as `BYTEA` in a partitioned database table with automatic partition management, scheduled migration, and background cleanup.

---

## Table of Contents

- [Overview](#overview)
- [File ID Format](#file-id-format)
- [Installation](#installation)
- [Quick Start](#quick-start)
  - [Several keyed storages](#several-keyed-storages)
  - [Reusing an NpgsqlDataSource](#reusing-an-npgsqldatasource)
  - [Without DI](#without-di)
- [CRUD Examples](#crud-examples)
- [Partitioning](#partitioning)
- [Scheduled Maintenance](#scheduled-maintenance)
- [Settings Reference](#settings-reference)
- [Dependencies](#dependencies)

---

## Overview

`PostgresFileStorage` implements `IFileStorage` backed by PostgreSQL. File binary data is stored as `BYTEA` columns in a partitioned table. Key characteristics:

- **Keyed registration** — every registration owns its table, its settings, its migration and its cleanup schedule; several independent storages coexist in one container
- **Automatic partitioning** — declarative list + range partitioning via `Sa.Partitional.PostgreSql`
- **Automatic data source** — a chained `AddDataSource(...)` is optional: without one the storage reuses the default `IPgDataSource` or an already-registered `NpgsqlDataSource`
- **Upsert semantics** — `ON CONFLICT DO UPDATE` handles re-uploads transparently
- **Scheduled migrations** — background job pre-creates future partitions
- **Background cleanup** — drops old partitions beyond retention period
- **Non-seekable stream handling** — buffers unseekable streams into `RecyclableMemoryStreamManager`
- **Timestamp in File ID** — includes Unix seconds for range partition resolution

---

## File ID Format

```
pg://{basket}/{tenantId}/{unixTimestamp}/{fileName}
```

**Examples:**
- `pg://files/42/1751347200/report.pdf`
- `pg://docs/7/1751347200/invoice.csv`
- `pg://share/100/1751347200/data.bin`

> The timestamp is the Unix epoch seconds of the upload date (UTC midnight). It determines which partition the row belongs to.

---

## Installation

```powershell
dotnet add package Sa.HybridFileStorage.Postgres
```

This package depends on `Sa.Data.PostgreSql` and `Sa.Partitional.PostgreSql`.

---

## Quick Start

Registration is **keyed**: every call takes a registration key, and each key produces an
independent storage with its own options (its own `TableName`, retention and partition
settings), its own partitional module, and its own keyed `IFileStorage`. The same instance is
also aliased as an *unkeyed* `IFileStorage`, which is what the hybrid container collects into
the failover chain — several keyed storages are tried in registration order and the first
success ends the operation.

```csharp
using Sa.HybridFileStorage.Postgres;

// One storage per key, each owning its table.
builder.Services.AddSaPostgreSqlFileStorageChained("files", b => b
    .Options(ob => ob.Configure(o =>
    {
        o.TableName = "files";
        o.StorageType = "pg";
        o.Basket = "share";
        o.ExpireDays = 365 * 3;               // drop this storage's partitions after 3 years
    })))
    .AddDataSource(ds => ds
        .Options(ob => ob.Configure(o => o.ConnectionString =
            "Host=localhost;Database=mydb;Username=postgres;Password=password")));
```

**`TableName` is required and unique.** It has no default: every registration must name the table
it owns, and two registrations may not declare the same table — they would both run migration and
cleanup over one physical table, so the collision is rejected at startup.

When the data source is already registered by something else, skip the chaining overload — the
storage resolves it automatically (see [Reusing an NpgsqlDataSource](#reusing-an-npgsqldatasource)):

```csharp
builder.Services.AddSaPostgreSqlFileStorage("binary", b => b
    .Options(ob => ob.Configure(o =>
    {
        o.TableName = "binary_data";
        o.SchemaName = "storage";
        o.PartByRange = PartByRange.Month;
    })));
```

Resolve a specific storage by key, or let the hybrid pipeline collect all of them:

```csharp
IFileStorage files  = sp.GetRequiredKeyedService<IFileStorage>("files");
IFileStorage binary = sp.GetRequiredKeyedService<IFileStorage>("binary");

// `sp.GetServices<IFileStorage>()` returns one instance per registration,
// in registration order — the order the hybrid failover chain tries them.
```

### Reusing an NpgsqlDataSource

A chained `AddDataSource(...)` is optional. When no data source is configured for the storage,
it resolves one in this order:

1. a keyed `IPgDataSource` under this registration's module key (the chained `AddDataSource`);
2. the default (unkeyed) `IPgDataSource`, if `AddSaPostgreSqlDataSource(...)` was called;
3. an `NpgsqlDataSource` already registered in the container — e.g. via
   `Npgsql.DependencyInjection`'s `AddNpgsqlDataSource(...)` or
   `services.AddSingleton<NpgsqlDataSource>(...)` — wrapped so it **reuses the same connection
   pool** and is never disposed by the storage.

```csharp
// The app already owns a pool; the storage rides on it — no connection string here at all.
builder.Services.AddNpgsqlDataSource("Host=db.internal;Database=mydb;Username=u;Password=p");
builder.Services.AddSaPostgreSqlFileStorage("files", b => b
    .Options(ob => ob.Configure(o => o.TableName = "files")));
```

With no `IPgDataSource` and no `NpgsqlDataSource` at all, resolving the storage fails with an
`InvalidOperationException` naming both missing registrations.

### Without DI

```csharp
var storage = new PostgresFileStorage(
    dataSource: IPgDataSource.Create("Host=localhost;Database=mydb;..."),
    partManager: /* an IPartitionManager for the table, e.g. from Sa.Partitional.PostgreSql */,
    streamManager: new RecyclableMemoryStreamManager(),
    options: new PostgresFileStorageOptions { TableName = "files" },
    timeProvider: TimeProvider.System);
```

> The DI registration is the intended path: it wires the partitional module (its migration and
> cleanup schedules) automatically. The constructor alone skips that wiring.

### Schema resolution

`SchemaName` is optional. When left `null` the schema is resolved once at startup, in this
order:

1. the explicit `SchemaName`, if set;
2. the first entry of the resolved data source's search path
   (`...;Search Path=storage,public` → `storage`);
3. `public`.

`GetSearchPath()` parses the connection string, so no database round trip is involved.

### Validation timing

Validation runs through the standard options pipeline — at the **first read** of the options (the
storage, the DDL or the schema resolver materialises them), or at host start with
`ValidateOnStart()`:

| Problem | Failure |
|---------|---------|
| `TableName`, `SchemaName` not a bare SQL identifier (`[A-Za-z_][A-Za-z0-9_]*`, ≤ 63 chars) | `OptionsValidationException` naming the property |
| `TableName` empty/not set | `OptionsValidationException` |
| `StorageType` > 10 chars, or containing `:`, `/` or `\` (it becomes the file ID scheme) | `OptionsValidationException` |
| `Basket` outside 3–63 chars, not starting with a letter/`_`, or containing a path separator | `OptionsValidationException` |
| `ExpireDays`, `MigrationScheduleForwardDays` `< 1` | `OptionsValidationException` |
| Two registrations declaring the same `TableName` | `OptionsValidationException` naming both keys |
| A second registration with the same **key** | `InvalidOperationException` at registration |
| `key` null/empty/whitespace | `ArgumentException` at registration |

`TableName` is used both for the DDL and for every query, so it is rejected rather than quietly
rewritten — `"\"files\""` used to be silently trimmed, which produced a table the provider then
failed to find.

---

## CRUD Examples

### Upload from Stream

```csharp
using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Hello, Postgres!"));
var result = await storage.UploadAsync(
    new UploadFileInput { FileName = "hello.txt", TenantId = 42 },
    stream, ct);

Console.WriteLine(result.FileId);
// pg://files/42/1751347200/hello.txt
// (timestamp = today's UTC midnight as Unix seconds)
```

### Upload Non-Seekable Stream

```csharp
// Non-seekable streams are automatically buffered into RecyclableMemoryStreamManager
await using var nonSeekable = CreateNonSeekableStream();

var result = await storage.UploadAsync(
    new UploadFileInput { FileName = "blob.dat", TenantId = 7 },
    nonSeekable, ct);

// Internally: copied → buffered → upserted → buffer recycled
```

### Download to Memory

```csharp
byte[]? downloaded = default;
await storage.DownloadAsync(result.FileId, async (stream, token) =>
{
    downloaded = await stream.ReadAllBytesAsync(token);
}, ct);
```

### Download Direct Processing

```csharp
await storage.DownloadAsync(result.FileId, async (stream, token) =>
{
    // Process stream directly — no intermediate buffering
    using var reader = new BinaryReader(stream);
    while (reader.ReadByte() is byte b)
    {
        // ...
    }
}, ct);
```

### Get Metadata

```csharp
var metadata = await storage.GetMetadataAsync(result.FileId, ct);
if (metadata != null)
{
    Console.WriteLine($"Basket: {metadata.Basket}");          // files
    Console.WriteLine($"Tenant: {metadata.TenantId}");        // 42
    Console.WriteLine($"Name:   {metadata.FileName}");        // hello.txt
    Console.WriteLine($"Type:   {metadata.StorageType}");     // pg
}
```

### Delete

```csharp
bool deleted = await storage.DeleteAsync(result.FileId, ct);
// Parses tenantId and timestamp from File ID for targeted DELETE
```

---

## Partitioning

Files are stored in a partitioned table with dual partitioning strategy:

1. **List partitioning** — by `(tenant_id, basket)` tuple
2. **Range partitioning** — by `created_at` (date)

### Schema auto-creation

The provider uses `Sa.Partitional.PostgreSql` to manage partitions:

```sql
-- Auto-created table structure:
CREATE TABLE public.files (
    id         TEXT NOT NULL,
    name       VARCHAR(512) NOT NULL,
    size       BIGINT NOT NULL,
    file_ext   VARCHAR(64) NOT NULL,
    tenant_id  INT NOT NULL,
    basket     VARCHAR(63) NOT NULL,
    data       BYTEA NOT NULL,
    created_at BIGINT NOT NULL       -- range partitioning key, Unix seconds
) PARTITION BY RANGE (created_at);

-- Each (tenant_id, basket) pair gets its own list partition within each date range
```

### Partition strategies

| Strategy | `PartByRange` value | Use case |
|----------|------------------|----------|
| Day | `PartByRange.Day` | High-volume systems, fine-grained cleanup |
| Month | `PartByRange.Month` | Medium volume, balanced granularity |
| Year | `PartByRange.Year` | Low volume, simple management |

### Migration schedule

Every registration configures its **own** migration schedule. New partitions are pre-created in
advance (default: `MigrationScheduleForwardDays = 2`) via a background job:

```csharp
options.MigrationScheduleForwardDays = 2;
```

### Cleanup schedule

Every registration configures its **own** cleanup schedule: old partitions beyond the
registration's retention period are dropped:

```csharp
options.ExpireDays = 365 * 3;  // drop this storage's partitions older than 3 years
```

---

## Scheduled Maintenance

Each registration registers its own two background jobs:

| Job | Purpose | Configuration |
|-----|---------|--------------|
| **Migration** | Pre-create this storage's upcoming partitions | `forwardDays`, `asBackgroundJob` |
| **Cleanup** | Drop this storage's old partitions after its retention | `dropPartsAfterRetention` (TimeSpan) |

Both run as background hosted services and use the same PostgreSQL connection pool as the
storage.

---

## Settings Reference

### PostgresFileStorageOptions

| Property | Description | Default |
|----------|-------------|---------|
| `SchemaName` | PostgreSQL schema; `null` means auto-detect (see above) | `null` → search path / `public` |
| `TableName` | Table name for file data — **required**, unique per registration | — |
| `StorageType` | Scheme prefix in File ID | `"pg"` |
| `Basket` | Scope/container name (used as list partition key) | `"share"` |
| `IsReadOnly` | Prevent write/delete operations | `false` |
| `PartByRange` | Range partitioning granularity | `PartByRange.Day` |
| `MigrationScheduleForwardDays` | Days ahead to pre-create partitions | `2` |
| `ExpireDays` | Retention period before partition drop (days) | `365 * 3` |

### Registration extensions

| Method | Description |
|--------|-------------|
| `AddSaPostgreSqlFileStorage(string key, Action<IPostgresFileStorageBuilder>?)` | Register a keyed provider; returns the service collection |
| `AddSaPostgreSqlFileStorage(string key, PostgresFileStorageOptions)` | Same, from a prepared instance (copied, not retained) |
| `AddSaPostgreSqlFileStorage(string key, IOptions<PostgresFileStorageOptions>)` | Same, from a ready-made options instance |
| `AddSaPostgreSqlFileStorageChained(string key, Action<IPostgresFileStorageBuilder>?)` | Same, returns `IPartConfiguration` for `.AddDataSource(...)` |

Registration keys must be unique per collection: a second call with the same key throws
`InvalidOperationException` — it would collide on the named options instance, the keyed
partitional module and the keyed `IFileStorage`.

### DI services

| Service | Purpose |
|---------|---------|
| `IFileStorage` (keyed by registration key) | The provider itself (singleton per key) |
| `IFileStorage` (unkeyed) | One alias per registration — what the hybrid container orders into the failover chain |
| `PostgresFileStorageOptions` (named by the registration key) | The storage's live options; read via `IOptionsMonitor<...>.Get(key)` |
| `RecyclableMemoryStreamManager` | Buffering for non-seekable streams (singleton, shared) |
| `IPartitionManager` (keyed) | This storage's partition maintenance, from `Sa.Partitional.PostgreSql` |
| `TimeProvider` | Defaults to `TimeProvider.System`; register your own to control `UploadedAt` |

---

## Dependencies

| Package | Purpose |
|---------|---------|
| `Sa.Data.PostgreSql` | Npgsql client (`IPgDataSource`) |
| `Sa.Partitional.PostgreSql` | Declarative partition management (`IPartitionManager`) |
| `Microsoft.IO.RecyclableMemoryStream` | Efficient memory buffering for non-seekable streams |

---

## Data Model

The underlying table structure:

| Column | Type | Purpose |
|--------|------|---------|
| `id` | `TEXT` | Canonical File ID (primary key part) |
| `name` | `VARCHAR(512)` | Original file name |
| `size` | `BIGINT` | File size in bytes |
| `file_ext` | `VARCHAR(64)` | File extension (e.g., "pdf", "png") |
| `tenant_id` | `INT` | Tenant identifier (list partition key) |
| `basket` | `VARCHAR(63)` | Container/scope name (list partition key) |
| `data` | `BYTEA` | Raw file binary content |
| `created_at` | `BIGINT` | Upload date (UTC midnight as Unix seconds, range partition key) |

`size` and `created_at` are `BIGINT`: the provider writes the stream length and the Unix
timestamp as 64-bit values.

---

## License

MIT