# Sa.HybridFileStorage.Postgres

PostgreSQL-backed file storage provider for `Sa.HybridFileStorage`. Stores files as `BYTEA` in a partitioned database table with automatic partition management, scheduled migration, and background cleanup.

---

## Table of Contents

- [Overview](#overview)
- [File ID Format](#file-id-format)
- [Installation](#installation)
- [Quick Start](#quick-start)
  - [Without DI](#without-di)
  - [With DI](#with-di)
- [CRUD Examples](#crud-examples)
- [Partitioning](#partitioning)
- [Scheduled Maintenance](#scheduled-maintenance)
- [Settings Reference](#settings-reference)
- [Dependencies](#dependencies)

---

## Overview

`PostgresFileStorage` implements `IFileStorage` backed by PostgreSQL. File binary data is stored as `BYTEA` columns in a partitioned table. Key characteristics:

- **Automatic partitioning** — declarative list + range partitioning via `Sa.Partitional.PostgreSql`
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

The provider is registered with `AddSaPostgreSqlFileStorage`, and the data source is
configured on the returned `IPartConfiguration`:

```csharp
using Sa.HybridFileStorage.Postgres;

builder.Services.AddSaPostgreSqlFileStorageChained(options =>
    {
        options.TableName = "files";
        options.StorageType = "pg";
        options.Basket = "share";
        options.ExpireDays = 365 * 3;                // drop partitions after 3 years
    })
    .AddDataSource(ds => ds
        .WithConnectionString("Host=localhost;Database=mydb;Username=postgres;Password=password"));
```

When the data source is already registered by something else, use the non-chaining overload:

```csharp
builder.Services.AddSaPostgreSqlFileStorage(options =>
{
    options.TableName = "binary_data";
    options.SchemaName = "storage";
    options.PgPartBy = PgPartBy.Month;
});
```

### Schema resolution

`SchemaName` is optional. When left `null` the schema is resolved once at startup, in this
order:

1. the explicit `SchemaName`, if set;
2. the first entry of the connection string's search path
   (`...;Search Path=storage,public` → `storage`);
3. `public`.

`GetSearchPath()` parses the connection string, so no database round trip is involved.

### Options are validated at registration

`AddSaPostgreSqlFileStorage` throws `ArgumentException` before registering anything if:

| Property | Requirement |
|----------|-------------|
| `TableName`, `SchemaName` | bare SQL identifier: `[A-Za-z_][A-Za-z0-9_]*`, at most 63 characters |
| `StorageType` | at most 10 characters, no `:`, `/` or `\` (it becomes the file ID scheme) |
| `Basket` | 3–63 characters, starts with a letter or `_`, no path separator |
| `ExpireDays`, `MigrationScheduleForwardDays` | `>= 1` |

`TableName` is used both for the DDL and for every query, so it is rejected rather than
quietly rewritten — `"\"files\""` used to be silently trimmed, which produced a table the
provider then failed to find.

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
    Console.WriteLine($"Корзина: {metadata.Basket}");       // files
    Console.WriteLine($"Тенант: {metadata.TenantId}");       // 42
    Console.WriteLine($"Имя: {metadata.FileName}");          // hello.txt
    Console.WriteLine($"Тип: {metadata.StorageType}");       // pg
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

| Strategy | `PgPartBy` value | Use case |
|----------|------------------|----------|
| Day | `PgPartBy.Day` | High-volume systems, fine-grained cleanup |
| Month | `PgPartBy.Month` | Medium volume, balanced granularity |
| Year | `PgPartBy.Year` | Low volume, simple management |

### Migration schedule

New partitions are pre-created in advance (default: 2 days ahead) via a background job:

```csharp
options.MigrationScheduleForwardDays = 2;
```

### Cleanup schedule

Old partitions beyond the retention period are dropped via a background job:

```csharp
options.ExpireDays = 365 * 3;  // drop partitions older than 3 years
```

---

## Scheduled Maintenance

Two background jobs are registered automatically:

| Job | Purpose | Configuration |
|-----|---------|--------------|
| **Migration** | Pre-create upcoming partitions | `forwardDays`, `asBackgroundJob` |
| **Cleanup** | Drop old partitions after retention | `dropPartsAfterRetention` (TimeSpan) |

Both run as background hosted services and use the same PostgreSQL connection pool.

---

## Settings Reference

### PostgresFileStorageOptions

| Property | Description | Default |
|----------|-------------|---------|
| `SchemaName` | PostgreSQL schema; `null` means auto-detect (see above) | `null` → search path / `public` |
| `TableName` | Table name for file data | `"files"` |
| `StorageType` | Scheme prefix in File ID | `"pg"` |
| `Basket` | Scope/container name (used as list partition key) | `"share"` |
| `IsReadOnly` | Prevent write/delete operations | `false` |
| `PgPartBy` | Range partitioning granularity | `PgPartBy.Day` |
| `MigrationScheduleForwardDays` | Days ahead to pre-create partitions | `2` |
| `ExpireDays` | Retention period before partition drop (days) | `365 * 3` |

### Registration extensions

| Method | Description |
|--------|-------------|
| `AddSaPostgreSqlFileStorage(Action<PostgresFileStorageOptions>?)` | Register the provider; returns the service collection |
| `AddSaPostgreSqlFileStorage(PostgresFileStorageOptions)` | Same, from a prepared instance (copied, not retained) |
| `AddSaPostgreSqlFileStorageChained(Action<PostgresFileStorageOptions>?)` | Same, returns `IPartConfiguration` for `.AddDataSource(...)` |

Registration is idempotent: a repeated call with equal options is a no-op, a call with
different options throws `InvalidOperationException` rather than duplicating the
partitioning setup, the schedules and the `IFileStorage`.

### DI services

| Service | Purpose |
|---------|---------|
| `IFileStorage` | The provider itself (singleton) |
| `RecyclableMemoryStreamManager` | Buffering for non-seekable streams (singleton, shared) |
| `IPartitionManager` | Partition maintenance, from `Sa.Partitional.PostgreSql` |
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
