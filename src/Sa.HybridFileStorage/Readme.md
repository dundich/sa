# Sa.HybridFileStorage

Hybrid file storage abstraction with automatic provider failover. Unifies multiple storage backends (FileSystem, S3, PostgreSQL) under a single resilient API — if one provider becomes unavailable, the system switches to another.

---

## Supported Storage Providers

| Provider | Class | Use Case |
|----------|-------|----------|
| **File System** | `FileSystemStorage` | Local development, on-premise deployments |
| **S3 Compatible** | `S3FileStorage` | Cloud storage (AWS S3, MinIO, etc.) |
| **PostgreSQL** | `PostgresFileStorage` | Database-embedded files, transactional consistency |
| **In-Memory** | `InMemoryFileStorage` | Testing, ephemeral scenarios |

---

## Key Features

- ✅ **Unified API** — Single interface for all storage providers
- ✅ **Basket-Tenant-based isolation** — Multi-tenant support with scoped buckets
- ✅ **Read-only mode** — Protect storage from accidental modifications
- ✅ **Streaming support** — Memory-efficient file transfers
- ✅ **Native AOT ready** — Full compatibility with .NET 10 Native AOT
- ✅ **Batch operations** — Efficient bulk file processing with parallelism
- ✅ **Interceptors** — Upload/download/delete lifecycle hooks

---

## File ID Format

All files are identified using a unified URI-like format:

```
{storageType}://{basket}/{tenantId}/{fileName}
```

**Examples:**
- `s3://share/42/document.pdf`
- `fs://root/100/report.xlsx`
- `pg://files/7/1773210911/some/data.bin`
- `mem://share/42/temp.txt`

---

## Quick Start

### Without DI

```csharp
using var memory = new InMemoryFileStorage(new InMemoryFileStorageOptions("share"));
var container = new HybridFileStorageContainer([memory]);
var storage = new HybridFileStorage(container, InterceptorContainer.Empty);

var stream = "Hello, HybridFileStorage!".ToStream();
var result = await storage.UploadAsync(
    "share",
    new UploadFileInput { FileName = "file.txt", TenantId = 42 },
    stream,
    ct);

await storage.DownloadAsync(result.FileId, async (fs, t) =>
{
    var content = await fs.ToStrAsync(t);
    Console.WriteLine(content);
});
```

### With DI

```csharp
builder.Services.AddSaHybridFileStorage(configure => configure
    .AddStorage(InMemoryFileStorage.New("share"))
    .AddLogging());

// Or register individual providers:
builder.Services.AddSaFileSystemFileStorage(new FileSystemStorageSettings
{
    BasePath = @"C:\data\files",
    Basket = "documents"
});

builder.Services.AddSaS3FileStorage(new S3FileStorageOptions
{
    Endpoint = "http://localhost:9000",
    AccessKey = "ROOTUSER",
    SecretKey = "ChangeMe123",
    Bucket = "mybucket",
    Basket = "uploads"
});

// Usage:
var storage = serviceProvider.GetRequiredService<IHybridFileStorage>();
```

---

## Settings

### FileSystemStorageSettings

| Property | Description | Default |
|----------|-------------|---------|
| `BasePath` | Root directory for files | *(required)* |
| `Basket` | Storage scope name | `"share"` |
| `StorageType` | Scheme prefix in File ID | `"fs"` |
| `IsReadOnly` | Prevent writes | `false` |
| `BufferSize` | Read/write buffer size | `256 KB` |

### S3FileStorageOptions

| Property | Description | Default |
|----------|-------------|---------|
| `Endpoint` | S3 endpoint URL | *(required)* |
| `AccessKey` | S3 access key | *(required)* |
| `SecretKey` | S3 secret key | *(required)* |
| `Bucket` | Bucket name | *(required)* |
| `Basket` | Storage scope name | `"share"` |
| `Region` | Region for SigV4 | `"eu-central-1"` |
| `IsReadOnly` | Prevent writes | `false` |

### PostgresFileStorageOptions

| Property | Description |
|----------|-------------|
| `SchemaName` | PostgreSQL schema |
| `TableName` | Table name for file data |
| `PartOptions.PgPartBy` | Partitioning strategy (day/month/year/list/range) |
| `CleanupOptions.ExpireDays` | Auto-cleanup threshold |
| `StorageOptions.IsReadOnly` | Prevent writes |

### InMemoryFileStorageOptions

| Property | Description | Default |
|----------|-------------|---------|
| `Basket` | Storage scope name | `"share"` |
| `IsReadOnly` | Prevent writes | `false` |

---

## Batch Operations

`HybridFileStorageExtensions` provides high-level methods for bulk file operations with built-in parallelism, error handling, and progress reporting.

```csharp
// Copy from local filesystem
var result = await storage.CopyFromFileAsync(
    @"C:\temp\document.pdf",
    "archive",
    new UploadFileInput { FileName = "archived.pdf", TenantId = 42 });

// Copy between baskets/scopes
var moved = await storage.CopyToBasketAsync(
    "s3://share/42/doc.pdf",
    "backup");

// Batch copy with parallelism and progress
var batchResult = await storage.CopyToScopeBatchAsync(
    fileIds: ["s3://share/1/a.txt", "s3://share/2/b.txt"],
    basket: "archive",
    options: new BatchOptions
    {
        MaxDegreeOfParallelism = 8,
        ContinueOnError = true,
        Progress = new Progress<BatchOperationProgress>()
    });

foreach (var ok in batchResult.Succeeded)
    Console.WriteLine($"Copied: {ok.FileId}");

foreach (var err in batchResult.Failed)
    Console.WriteLine($"Failed #{err.Index}: {err.FileId} — {err.Exception.Message}");
```

### BatchResult<T>

| Member | Type | Description |
|--------|------|-------------|
| `Succeeded` | `IReadOnlyList<T>` | Successful results |
| `Failed` | `IReadOnlyList<BatchError>` | Errors with file ID and exception |
| `Total` | `int` | Total items processed |
| `HasErrors` | `bool` | Whether any operation failed |
| `ThrowIfHasErrors()` | `void` | Throws `BatchOperationException<T>` if failures occurred |

### BatchOptions

| Property | Description | Default |
|----------|-------------|---------|
| `MaxDegreeOfParallelism` | Concurrent operations | `4` |
| `ContinueOnError` | Keep going after failures | `true` |
| `OperationTimeout` | Per-operation timeout | `0` (infinite) |
| `Progress` | Progress reporter | `null` |

---

## Interceptors

Lifecycle hooks for upload/download/delete operations.

```csharp
public interface IUploadInterceptor
{
    ValueTask<bool> CanUploadAsync(IFileStorage storage, UploadFileInput input, Stream fileStream, CancellationToken ct);
    ValueTask AfterUploadAsync(IFileStorage storage, StorageResult result, CancellationToken ct);
    ValueTask OnUploadErrorAsync(IFileStorage storage, Exception exception, CancellationToken ct);
}

public interface IDownloadInterceptor { /* analogous Can/After/Error */ }
public interface IDeleteInterceptor { /* analogous Can/After/Error */ }
```

Register interceptors via fluent builder:

```csharp
services.AddSaHybridFileStorage(cfg => cfg.ConfigureInterceptors((sp, container) =>
{
    container.AddUploadInterceptor(myCustomInterceptor);
    container.AddDownloadInterceptor(loggingInterceptor);
}));
```

Built-in `LoggingInterceptor` is available via `.AddLogging()`.

---

## Read-Only Mode

Set `IsReadOnly = true` on any storage provider to prevent writes. Attempted writes throw `HybridFileStorageWritableException`:

```csharp
builder.Services.AddSaFileSystemFileStorage(settings =>
{
    settings.BasePath = @"C:\readonly\data";
    settings.IsReadOnly = true;
});
```

---

## Domain Types

### StorageResult

```csharp
public sealed record StorageResult(
    string FileId,
    string AbsoluteUrl,
    string StorageType,
    DateTimeOffset UploadedAt);
```

### UploadFileInput

```csharp
public sealed record UploadFileInput
{
    public int TenantId { get; init; }
    public string FileName { get; init; }
    public static UploadFileInput Empty { get; }
}
```

### FileMetadata

```csharp
public sealed class FileMetadata
{
    public required string Basket { get; init; }
    public required string FileName { get; init; }
    public int TenantId { get; init; }
    public required string StorageType { get; init; }
}
```

---

## Exceptions

| Exception | When thrown |
|-----------|------------|
| `HybridFileStorageNoAvailableException` | No storage found for the requested basket |
| `HybridFileStorageWritableException` | Write attempted on read-only storage |
| `HybridFileStorageAggregateException` | Multiple provider errors aggregated |
| `BatchOperationException<T>` | Batch with failures and `ContinueOnError = false` |

---

## Project Structure

```
src/Sa.HybridFileStorage/
├── IHybridFileStorage.cs           # Main interface
├── HybridFileStorage.cs             # Implementation with failover
├── HybridFileStorageContainer.cs    # Provider container
├── HybridStorageBuilder.cs          # Fluent builder
├── HybridFileStorageExtensions.cs   # Batch operations
├── Setup.cs                         # DI extensions
├── FileMetadata.cs                  # Metadata DTO
├── BatchResult.cs                   # Batch result types
└── Interceptors/                    # Upload/download/delete hooks

src/Sa.HybridFileStorage.FileSystem/  # Filesystem provider
src/Sa.HybridFileStorage.S3/          # S3 provider
src/Sa.HybridFileStorage.Postgres/    # PostgreSQL provider
```

---

## License

MIT
