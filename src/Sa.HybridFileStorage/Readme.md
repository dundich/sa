# Sa.HybridFileStorage

Hybrid file storage abstraction with automatic provider failover. Unifies multiple storage backends (FileSystem, S3, PostgreSQL) under a single resilient API — if one provider becomes unavailable, the system switches to another.

---

## Table of Contents

- [Supported Storage Providers](#supported-storage-providers)
- [Key Features](#key-features)
- [File ID Format](#file-id-format)
- [Quick Start](#quick-start)
  - [Without DI](#without-di)
  - [With DI (Generic Host)](#with-di-generic-host)
- [CRUD Examples](#crud-examples)
  - [Upload](#upload)
  - [Download](#download)
  - [Delete](#delete)
  - [Get Metadata](#get-metadata)
- [Cross-Basket Copying](#cross-basket-copying)
- [Batch Operations](#batch-operations)
- [Interceptors](#interceptors)
- [Read-Only Mode](#read-only-mode)
- [Settings Reference](#settings-reference)
- [Domain Types](#domain-types)
- [Exceptions](#exceptions)
- [Project Structure](#project-structure)

---

## Supported Storage Providers

| Provider | Package | Class | Use Case |
|----------|---------|-------|----------|
| **In-Memory** | `Sa.HybridFileStorage` | `InMemoryFileStorage` | Testing, ephemeral scenarios |
| **File System** | `Sa.HybridFileStorage.FileSystem` | `FileSystemStorage` | Local development, on-premise deployments |
| **S3 Compatible** | `Sa.HybridFileStorage.S3` | `S3FileStorage` | Cloud storage (AWS S3, MinIO, etc.) |
| **PostgreSQL** | `Sa.HybridFileStorage.Postgres` | `PostgresFileStorage` | Database-embedded files, transactional consistency, partitioning |

---

## Key Features

- ✅ **Unified API** — Single `IHybridFileStorage` interface for all storage providers
- ✅ **Basket-Tenant isolation** — Multi-tenant support with scoped storage containers
- ✅ **Failover** — Automatic provider switching when one backend fails
- ✅ **Streaming support** — Memory-efficient file transfers via `Stream`
- ✅ **Native AOT ready** — Full compatibility with .NET 10 Native AOT
- ✅ **Batch operations** — Bulk file processing with configurable parallelism
- ✅ **Interceptors** — Upload/download/delete lifecycle hooks for cross-cutting concerns
- ✅ **Read-only mode** — Protect storage from accidental modifications

---

## File ID Format

All files are identified using a unified URI-like format:

```
{storageType}://{basket}/{tenantId}/{path}
```

Each storage provider adds its own path depth:

| Provider | File ID Example | Path Structure |
|----------|----------------|----------------|
| **In-Memory** | `mem://share/42/document.pdf` | `{basket}/{tenantId}/{fileName}` |
| **File System** | `fs://documents/100/report.xlsx` | `{basket}/{tenantId}/{fileName}` |
| **S3** | `s3://uploads/7/invoice.csv` | `{basket}/{tenantId}/{fileName}` |
| **PostgreSQL** | `pg://files/12/1751347200/photo.jpg` | `{basket}/{tenantId}/{unixTimestamp}/{fileName}` |

> **Note:** PostgreSQL includes a Unix timestamp in the path because it partitions by date. Other providers omit the timestamp.

### Parsing File IDs

Use the static `FileIdParser` utility:

```csharp
if (FileIdParser.TryParse("pg://files/42/1751347200/report.pdf", out var basket, out var tenantId, out var timestamp, out var fileName))
{
    Console.WriteLine($"Basket={basket}, Tenant={tenantId}, TS={timestamp}, Name={fileName}");
    // Basket=files, Tenant=42, TS=1751347200, Name=report.pdf
}
```

---

## Quick Start

### Without DI

```csharp
using Sa.HybridFileStorage;
using Sa.HybridFileStorage.Domain;

// 1. Create an in-memory storage provider
using var memory = new InMemoryFileStorage(new InMemoryFileStorageOptions("share"));

// 2. Build the hybrid container
var container = new HybridFileStorageContainer([memory]);
var storage = new HybridFileStorage(container, InterceptorContainer.Empty);

// 3. Upload a file
var stream = "Hello, HybridFileStorage!".ToUtf8Stream();
var result = await storage.UploadAsync(
    basket: "share",
    input: new UploadFileInput { FileName = "hello.txt", TenantId = 42 },
    fileStream: stream,
    cancellationToken: ct);

Console.WriteLine(result.FileId);  // mem://share/42/hello.txt

// 4. Download and process
bool wasFound = await storage.DownloadAsync(result.FileId, async (stream, token) =>
{
    using var reader = new StreamReader(stream, Encoding.UTF8);
    var content = await reader.ReadToEndAsync(token);
    Console.WriteLine(content);  // Hello, HybridFileStorage!
}, ct);

// 5. Delete
bool deleted = await storage.DeleteAsync(result.FileId, ct);
```

### With DI (Generic Host)

```csharp
using Microsoft.Extensions.Hosting;
using Sa.HybridFileStorage;
using Sa.HybridFileStorage.FileSystem;
using Sa.HybridFileStorage.S3;

var builder = Host.CreateApplicationBuilder(args);

// Register all providers at once via fluent builder
builder.Services.AddSaHybridFileStorage(cfg => cfg
    // In-Memory provider
    .ConfigureStorage((sp, c) => c.AddStorage(new InMemoryFileStorage()))

    // File System provider
    .ConfigureStorage((sp, c) => c.AddStorage(new FileSystemStorage(
        new FileSystemStorageSettings
        {
            BasePath = @"C:\data\files",
            Basket = "documents"
        })))

    // S3 provider
    .ConfigureStorage((sp, c) => c.AddStorage(
        new S3FileStorage(
            sp.GetRequiredService<IS3BucketClient>(),
            new S3FileStorageOptions
            {
                Endpoint = "http://localhost:9000",
                AccessKey = "ROOTUSER",
                SecretKey = "ChangeMe123",
                Bucket = "mybucket",
                Basket = "uploads"
            })))

    // Enable built-in logging interceptors
    .AddLogging());

var host = builder.Build();
var storage = host.Services.GetRequiredService<IHybridFileStorage>();

// Use it anywhere — injected into your services
```

#### Minimal DI registration

For quick setups, each provider has its own extension method:

```csharp
// In-Memory only
builder.Services.AddSaInMemoryFileStorage();

// File System only
builder.Services.AddSaFileSystemFileStorage(new FileSystemStorageSettings
{
    BasePath = @"C:\data\files",
    Basket = "documents"
});

// S3 only
builder.Services.AddSaS3FileStorage(new S3FileStorageOptions
{
    Endpoint = "http://localhost:9000",
    AccessKey = "ROOTUSER",
    SecretKey = "ChangeMe123",
    Bucket = "mybucket",
    Basket = "uploads"
});

// Then register the hybrid layer
builder.Services.AddSaHybridFileStorage(cfg => cfg.AddLogging());
```

---

## CRUD Examples

### Upload

Upload accepts a `basket` name (scope/container), metadata, and a `Stream`. The hybrid layer finds a writable provider matching the basket and uploads the file.

```csharp
// Upload from a Stream
using var stream = File.OpenRead(@"C:\temp\document.pdf");
var result = await storage.UploadAsync(
    basket: "documents",
    input: new UploadFileInput { FileName = "document.pdf", TenantId = 42 },
    fileStream: stream,
    cancellationToken: ct);

Console.WriteLine($"Uploaded: {result.FileId}");
// Output: fs://documents/42/document.pdf
```

Copy a local file directly:

```csharp
var result = await storage.CopyFromFileAsync(
    filePath: @"C:\temp\image.png",
    basket: "images",
    input: new UploadFileInput { FileName = "avatar.png", TenantId = 7 },
    ct: ct);
```

### Download

Download delegates the file stream to a callback `Func<Stream, CancellationToken, Task>`. This avoids loading the entire file into memory.

```csharp
// Process stream inline
bool found = await storage.DownloadAsync(result.FileId, async (stream, token) =>
{
    using var reader = new StreamReader(stream, Encoding.UTF8);
    string content = await reader.ReadToEndAsync(token);
    Console.WriteLine(content);
}, ct);

// Copy to another stream
using var destination = new FileStream(@"C:\output\copy.pdf", FileMode.Create);
await storage.DownloadAsync(result.FileId, async (source, token) =>
    await source.CopyToAsync(destination, 81920, token),
    ct);
```

### Delete

```csharp
bool deleted = await storage.DeleteAsync(result.FileId, ct);
if (deleted)
    Console.WriteLine("File removed.");
else
    Console.WriteLine("File not found.");
```

### Get Metadata

```csharp
var metadata = await storage.GetMetadataAsync(result.FileId, ct);
if (metadata != null)
{
    Console.WriteLine($"Basket: {metadata.Basket}");
    Console.WriteLine($"Tenant: {metadata.TenantId}");
    Console.WriteLine($"Name:   {metadata.FileName}");
    Console.WriteLine($"Type:   {metadata.StorageType}");
}
```

---

## Cross-Basket Copying

Move or duplicate files between baskets (even across different providers):

```csharp
// Copy within same basket
var copied = await storage.CopyToBasketAsync(
    fileId: "fs://documents/42/report.pdf",
    basket: "archive",
    ct: ct);

// Customise upload metadata during copy
var renamed = await storage.CopyToBasketAsync(
    fileId: "fs://documents/42/report.pdf",
    basket: "backup",
    configure: meta => new UploadFileInput
    {
        TenantId = meta.TenantId,
        FileName = $"renamed-{meta.FileName}"  // change the name
    },
    ct: ct);
```

---

## Batch Operations

High-throughput parallel file operations with progress reporting and error handling:

```csharp
// Batch copy with parallelism
var batchResult = await storage.CopyToScopeBatchAsync(
    fileIds:
    [
        "fs://documents/1/a.pdf",
        "fs://documents/2/b.pdf",
        "s3://uploads/3/c.pdf",
    ],
    basket: "archive",
    options: new BatchOptions
    {
        MaxDegreeOfParallelism = 8,
        ContinueOnError = true,
        OperationTimeout = TimeSpan.FromSeconds(30),
        Progress = new Progress<BatchOperationProgress>(p =>
        {
            Console.WriteLine($"{p.Completed}/{p.Total} — OK:{p.SuccessCount} Fail:{p.FailureCount}");
        })
    },
    ct: ct);

foreach (var ok in batchResult.Succeeded)
    Console.WriteLine($"Copied: {ok.FileId}");

foreach (var err in batchResult.Failed)
    Console.WriteLine($"Failed #{err.Index}: {err.FileId} — {err.Exception.Message}");

// Or throw on any failure
batchResult.ThrowIfHasErrors();  // throws BatchOperationException<StorageResult>
```

### BatchResult&lt;T&gt;

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
| `ContinueOnError` | Keep going after individual failures | `true` |
| `OperationTimeout` | Per-operation timeout (`0` = infinite) | `0` |
| `Progress` | `IProgress<BatchOperationProgress>` reporter | `null` |

---

## Interceptors

Lifecycle hooks for upload/download/delete operations. Implement one of the three interfaces:

```csharp
public interface IUploadInterceptor
{
    // Return false to reject the upload
    ValueTask<bool> CanUploadAsync(IFileStorage storage, UploadFileInput input, Stream fileStream, CancellationToken ct);
    ValueTask AfterUploadAsync(IFileStorage storage, StorageResult result, CancellationToken ct);
    ValueTask OnUploadErrorAsync(IFileStorage storage, Exception exception, CancellationToken ct);
}

public interface IDownloadInterceptor { /* CanDownloadAsync / AfterDownloadAsync / OnDownloadErrorAsync */ }
public interface IDeleteInterceptor  { /* CanDeleteAsync / AfterDeleteAsync / OnDeleteErrorAsync */ }
```

Example — reject uploads of specific filenames:

```csharp
public class DeniedExtensionInterceptor : IUploadInterceptor
{
    private static readonly HashSet<string> DeniedExtensions = ["exe", "bat", "cmd"];

    public ValueTask<bool> CanUploadAsync(IFileStorage storage, UploadFileInput input, Stream fileStream, CancellationToken ct)
    {
        var ext = Path.GetExtension(input.FileName)?.TrimStart('.').ToLowerInvariant();
        return ValueTask.FromResult(!DeniedExtensions.Contains(ext));
    }

    public ValueTask AfterUploadAsync(IFileStorage storage, StorageResult result, CancellationToken ct)
        => ValueTask.CompletedTask;

    public ValueTask OnUploadErrorAsync(IFileStorage storage, Exception exception, CancellationToken ct)
        => ValueTask.CompletedTask;
}
```

Register interceptors through the fluent builder:

```csharp
builder.Services.AddSaHybridFileStorage(cfg => cfg
    .ConfigureInterceptors((sp, container) =>
    {
        container.AddUploadInterceptor(new DeniedExtensionInterceptor());
        container.AddDownloadInterceptor(new LoggingDownloadInterceptor());
    }));
```

Built-in logging interceptors are available via `.AddLogging()`.

---

## Read-Only Mode

Set `IsReadOnly = true` on any provider to prevent writes. Attempted writes throw `HybridFileStorageWritableException`:

```csharp
builder.Services.AddSaFileSystemFileStorage(new FileSystemStorageSettings
{
    BasePath = @"C:\readonly\data",
    IsReadOnly = true  // uploads/deletes will fail
});
```

---

## Settings Reference

### FileSystemStorageSettings

| Property | Description | Default |
|----------|-------------|---------|
| `BasePath` | Root directory for files | *(required)* |
| `Basket` | Scope/container name | `"share"` |
| `StorageType` | Scheme prefix in File ID | `"fs"` |
| `IsReadOnly` | Prevent writes | `false` |

### S3FileStorageOptions

| Property | Description | Default |
|----------|-------------|---------|
| `Endpoint` | S3 endpoint URL | *(required)* |
| `AccessKey` | S3 access key | *(required)* |
| `SecretKey` | S3 secret key | *(required)* |
| `Bucket` | Bucket name | *(required)* |
| `Basket` | Scope/container name | `"share"` |
| `Region` | Region for SigV4 signing | `"eu-central-1"` |
| `StorageType` | Scheme prefix in File ID | `"s3"` |
| `IsReadOnly` | Prevent writes | `false` |

### PostgresFileStorageOptions

| Property | Description | Default |
|----------|-------------|---------|
| `StorageOptions.SchemaName` | PostgreSQL schema | `"public"` |
| `StorageOptions.TableName` | Table for file data | `"files"` |
| `StorageOptions.StorageType` | Scheme prefix in File ID | `"pg"` |
| `PartOptions.Basket` | Scope/container name | `"share"` |
| `PartOptions.PgPartBy` | Partitioning granularity | `PgPartBy.Day` |
| `PartOptions.MigrationScheduleForwardDays` | Days ahead to pre-create partitions | `2` |
| `CleanupOptions.ExpireDays` | Auto-cleanup threshold (days) | `365 * 3` |
| `StorageOptions.IsReadOnly` | Prevent writes | `false` |

### InMemoryFileStorageOptions

| Property | Description | Default |
|----------|-------------|---------|
| `Basket` | Scope/container name | `"share"` |
| `MaxSizeBytes` | Total byte limit (`0` = unlimited) | `0` |
| `IsReadOnly` | Prevent writes | `false` |

---

## Domain Types

### StorageResult

Returned by upload operations. Contains the canonical File ID and a publicly accessible URL.

```csharp
public sealed record StorageResult(
    string FileId,          // e.g. "fs://documents/42/report.pdf"
    string AbsoluteUrl,     // e.g. "C:\data\files\documents\42\report.pdf"
    string StorageType,     // e.g. "fs", "s3", "pg", "mem"
    DateTimeOffset UploadedAt);
```

### UploadFileInput

Input metadata for uploads.

```csharp
public sealed record UploadFileInput
{
    public int TenantId { get; init; }              // defaults to 0
    public string FileName { get; init; } = "";     // required at validation
    public static UploadFileInput Empty { get; }    // pre-created empty instance
}
```

### FileMetadata

Read-only metadata retrieved via `GetMetadataAsync`.

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
| `HybridFileStorageNoAvailableException` | No storage provider found for the requested basket, or all providers failed |
| `HybridFileStorageWritableException` | Write attempted on read-only storage |
| `HybridFileStorageAggregateException` | Multiple provider errors aggregated during failover |
| `BatchOperationException<T>` | Batch operation had failures and `ContinueOnError = false` |

---

## Project Structure

```
src/Sa.HybridFileStorage/                          # Core library (NuGet: Sa.HybridFileStorage)
├── IHybridFileStorage.cs                          # Main interface
├── HybridFileStorage.cs                           # Implementation with failover + interceptors
├── HybridFileStorageContainer.cs                  # Provider container
├── HybridStorageBuilder.cs                        # Fluent DI builder
├── HybridFileStorageExtensions.cs                 # Batch operations (CopyFromFile, CopyToBasket, …)
├── Setup.cs                                       # DI extensions (AddSaHybridFileStorage, AddSaInMemoryFileStorage)
├── FileIdParser.cs                                # File ID parsing/formatting utility
├── FileMetadata.cs                                # Metadata DTO
├── InMemoryFileStorage.cs                         # In-memory provider
├── InMemoryFileStorageOptions.cs                  # Options for in-memory
├── BatchResult.cs, BatchOptions.cs, …            # Batch operation types
└── Interceptors/                                  # Upload/download/delete hooks
    ├── IUploadInterceptor.cs
    ├── IDownloadInterceptor.cs
    ├── IDeleteInterceptor.cs
    ├── UploadLoggingInterceptor.cs
    ├── DownloadLoggingInterceptor.cs
    └── DeleteLoggingInterceptor.cs

src/Sa.HybridFileStorage.FileSystem/               # File system provider (NuGet: Sa.HybridFileStorage.FileSystem)
src/Sa.HybridFileStorage.S3/                       # S3 provider (NuGet: Sa.HybridFileStorage.S3)
src/Sa.HybridFileStorage.Postgres/                 # PostgreSQL provider (NuGet: Sa.HybridFileStorage.Postgres)
```

---

## License

MIT
