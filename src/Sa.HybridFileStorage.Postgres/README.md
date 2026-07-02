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

### Without DI

```csharp
using Sa.HybridFileStorage.Postgres;
using Sa.HybridFileStorage.Domain;

// Configure via fluent builder
var configurator = new PostgresFileStorageConfiguration(services);

// Or register through DI extension
builder.Services.AddSaPostgreSqlFileStorage(cfg => cfg
    .AddDataSource(ds => ds
        .WithConnectionString("Host=localhost;Database=mydb;Username=postgres;Password=password")
        .WithSearchPath("public"))
    .WithSchemaName("public")
    .WithTableName("files")
    .WithStorageType("pg")
    .ConfigureOptions((sp, options) =>
    {
        // Customize partitioning
        options.PartOptions.Basket = "files";
        options.PartOptions.PgPartBy = PgPartBy.Day;
        options.PartOptions.MigrationScheduleForwardDays = 2;

        // Customize cleanup
        options.CleanupOptions.ExpireDays = 365 * 3;  // 3 years
    }));
```

### With DI

```csharp
using Sa.HybridFileStorage.Postgres;

builder.Services.AddSaPostgreSqlFileStorage(cfg => cfg
    .AddDataSource(ds => ds
        .WithConnectionString("Host=db.example.com;Database=app;Username=app_user;Password=secret")
        .WithSearchPath("storage"))
    .WithTableName("binary_data")
    .WithSchemaName("storage")
    .ConfigureOptions((sp, opts) =>
    {
        opts.PartOptions.Basket = "attachments";
        opts.PartOptions.PgPartBy = PgPartBy.Month;
        opts.CleanupOptions.ExpireDays = 730;  // 2 years
    }));
```

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
    name       TEXT NOT NULL,
    size       INT NOT NULL,
    file_ext   TEXT NOT NULL,
    tenant_id  INT NOT NULL,
    basket     TEXT NOT NULL,
    data       BYTEA NOT NULL,
    created_at TIMESTAMPTZ NOT NULL  -- used for range partitioning
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
.ConfigureOptions((sp, opts) =>
{
    opts.PartOptions.MigrationScheduleForwardDays = 2;
})
```

### Cleanup schedule

Old partitions beyond the retention period are dropped via a background job:

```csharp
.ConfigureOptions((sp, opts) =>
{
    opts.CleanupOptions.ExpireDays = 365 * 3;  // drop partitions older than 3 years
})
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
| `StorageOptions.SchemaName` | PostgreSQL schema | `"public"` |
| `StorageOptions.TableName` | Table name for file data | `"files"` |
| `StorageOptions.StorageType` | Scheme prefix in File ID | `"pg"` |
| `StorageOptions.IsReadOnly` | Prevent write/delete operations | `false` |
| `PartOptions.Basket` | Scope/container name (used as list partition key) | `"share"` |
| `PartOptions.PgPartBy` | Range partitioning granularity | `PgPartBy.Day` |
| `PartOptions.MigrationScheduleForwardDays` | Days ahead to pre-create partitions | `2` |
| `CleanupOptions.ExpireDays` | Retention period before partition drop (days) | `365 * 3` |

### IPostgresFileStorageConfiguration (fluent builder)

| Method | Description |
|--------|-------------|
| `AddDataSource(Action<IPgDataSourceSettingsBuilder>?)` | Configure PostgreSQL connection |
| `WithSchemaName(string)` | Override schema name |
| `WithTableName(string)` | Override table name |
| `WithStorageType(string)` | Override storage type identifier |
| `AsReadOnly()` | Mark as read-only |
| `ConfigureOptions(Action<IServiceProvider, PostgresFileStorageOptions>)` | Late-stage customization |

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
| `name` | `TEXT` | Original file name |
| `size` | `INT` | File size in bytes |
| `file_ext` | `TEXT` | File extension (e.g., "pdf", "png") |
| `tenant_id` | `INT` | Tenant identifier (list partition key) |
| `basket` | `TEXT` | Container/scope name (list partition key) |
| `data` | `BYTEA` | Raw file binary content |
| `created_at` | `TIMESTAMPTZ` | Upload date (UTC midnight, range partition key) |

---

## License

MIT
