# Hybrid File Storage

## IHybridFileStorage Interface

The `IHybridFileStorage` interface enhances the resilience and availability of file data in applications that require reliable storage management.

This interface defines a contract for hybrid file storage systems capable of handling file operations such as uploading, downloading, and deleting files. The integration of multiple storage providers (such as file system, S3, and PostgreSQL) ensures reliable file storage, as the system can automatically switch between different providers in the event that one becomes unavailable.

## Supported Storage Providers

| Provider | Class | Use Case |
|----------|-------|----------|
| **File System** | `FileSystemStorage` | Local development, on-premise deployments |
| **S3 Compatible** | `S3FileStorage` | Cloud storage (AWS S3, MinIO, etc.) |
| **PostgreSQL** | `PostgresFileStorage` | Database-embedded files, transactional consistency |

## Key Features

- ✅ **Unified API** — Single interface for all storage providers
- ✅ **Scope-based isolation** — Multi-tenant support via scopes
- ✅ **Read-only mode** — Protect storage from accidental modifications
- ✅ **Streaming support** — Memory-efficient file transfers
- ✅ **Native AOT ready** — Full compatibility with .NET 10 Native AOT
- ✅ **Batch operations** — Efficient bulk file processing

## Batch Operations

The `HybridFileStorageExtensions` class provides high-level methods for bulk file operations with built-in parallelism, error handling, and progress reporting.


## File ID Format

All files are identified using a unified URI-like format:

```
{storageType}://{scope}/{tenantId}/{fileName}
```

**Examples:**
- `s3://share/42/document.pdf`
- `fs://root/100/report.xlsx`
- `pg://files/7/1773210911/some/data.bin`

## Installation

```bash
dotnet add package Sa.HybridFileStorage
```


## Usage Example

```csharp

// di
builder.Services.AddSaHybridStorage((_, b) => b.AddStorage(new InMemoryFileStorage()));

// some test
using var stream = "Hello, HybridFileStorage!".ToStream();

await storage.UploadAsync(
    new UploadFileInput { FileName = "file.txt" },
    stream,
    cancellationToken);

await storage.DownloadAsync(
    result.FileId,
    async (fs, t) => actual = await fs.ToStrAsync(t),
    cancellationToken);

```
