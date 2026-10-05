# Sa.HybridFileStorage.S3

S3-compatible cloud storage provider for `Sa.HybridFileStorage`. Wraps Minio/AWS S3 with automatic bucket creation, MIME type detection, and streaming file I/O.

---

## Table of Contents

- [Overview](#overview)
- [File ID Format](#file-id-format)
- [Installation](#installation)
- [Quick Start](#quick-start)
  - [Without DI](#without-di)
  - [With DI](#with-di)
- [CRUD Examples](#crud-examples)
- [Settings Reference](#settings-reference)
- [Dependencies](#dependencies)

---

## Overview

`S3FileStorage` implements `IFileStorage` backed by any S3-compatible service (AWS S3, MinIO, DigitalOcean Spaces, etc.). Key characteristics:

- **Auto bucket creation** — creates the target bucket on first upload if it doesn't exist (thread-safe, single-flight)
- **MIME type detection** — resolves content type from file extension using `MimeTypeMap`
- **Streaming uploads/downloads** — uses `IS3BucketClient` for memory-efficient transfers
- **Thread-safe initialization** — concurrent `EnsureBucket` calls are deduplicated via `Interlocked.CompareExchange`

---

## File ID Format

```
s3://{basket}/{tenantId}/{fileName}
```

**Examples:**
- `s3://uploads/42/document.pdf`
- `s3://avatars/7/profile.png`
- `s3://backups/100/database.sql`

---

## Installation

```powershell
dotnet add package Sa.HybridFileStorage.S3
```

This package depends on `Sa.Data.S3` which provides the underlying `IS3BucketClient`.

---

## Quick Start

### Without DI

```csharp
using Sa.HybridFileStorage.S3;
using Sa.HybridFileStorage.Domain;
using Sa.Data.S3;

// Configure S3 client
var setup = new S3BucketClientSetupOptions
{
    Endpoint = "http://localhost:9000",
    AccessKey = "ROOTUSER",
    SecretKey = "ChangeMe123",
    Bucket = "mybucket",
    Region = "us-east-1"
};

var client = new S3BucketClient(setup);

var options = new S3FileStorageOptions
{
    Endpoint = "http://localhost:9000",
    AccessKey = "ROOTUSER",
    SecretKey = "ChangeMe123",
    Bucket = "mybucket",
    Basket = "uploads",
    Region = "us-east-1"
};

using var storage = new S3FileStorage(client, options);

// Upload
using var stream = File.OpenRead(@"C:\temp\document.pdf");
var result = await storage.UploadAsync(
    new UploadFileInput { FileName = "document.pdf", TenantId = 42 },
    stream, ct);

Console.WriteLine(result.FileId);     // s3://uploads/42/document.pdf
Console.WriteLine(result.AbsoluteUrl); // http://localhost:9000/mybucket/uploads/42/document.pdf

// Download
bool found = await storage.DownloadAsync(result.FileId, async (fs, token) =>
{
    using var reader = new StreamReader(fs, Encoding.UTF8);
    var content = await reader.ReadToEndAsync(token);
    Console.WriteLine(content);
}, ct);

// Delete
bool deleted = await storage.DeleteAsync(result.FileId, ct);
```

### With DI

`AddSaS3FileStorage` takes the standard options callback, so configuration goes through
`Configure` / `PostConfigure` / `Validate` like any other options type:

```csharp
using Sa.HybridFileStorage.S3;

builder.Services.AddSaS3FileStorage(o => o.Configure(x =>
{
    x.Endpoint = "http://localhost:9000";
    x.AccessKey = "ROOTUSER";
    x.SecretKey = "ChangeMe123";
    x.Bucket = "mybucket";
    x.Basket = "uploads";
    x.Region = "us-east-1";
}));

// The DI container resolves IS3BucketClient automatically
```

Transport tuning is not a nested object here — `TotalRequestTimeout`, `ConnectionPoolLifetime`
and `HandlerLifetime` are properties of the same options instance the bucket client is built
from.

### From a configuration section

```csharp
// appsettings.json:
// { "S3FileStorage": { "Endpoint": "http://localhost:9000", "AccessKey": "…", "SecretKey": "…", "Bucket": "mybucket", "Basket": "uploads" } }
builder.Services.AddSaS3FileStorage(configSectionPath: "S3FileStorage");
```

The section is bound **first**, so a `Configure` call inside the callback has the last word. The
callback also runs after the registration's own `PostConfigure` and `Validate`, so your checks add
to the built-in ones instead of replacing them.

---

## CRUD Examples

### Upload from Stream

```csharp
using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Hello, S3!"));
var result = await storage.UploadAsync(
    new UploadFileInput { FileName = "hello.txt", TenantId = 1 },
    stream, ct);

// File stored at: mybucket/uploads/1/hello.txt
// Content-Type: text/plain (auto-detected from extension)
```

### Upload Large Files

```csharp
// Streaming large files without loading into memory
await using var fs = new FileStream(@"C:\large\data.zip", new FileStreamOptions
{
    Mode = FileMode.Open,
    Access = FileAccess.Read,
    Share = FileShare.Read,
    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
});

var result = await storage.UploadAsync(
    new UploadFileInput { FileName = "data.zip", TenantId = 5 },
    fs, ct);
```

### Download to Memory

```csharp
byte[]? downloaded = default;
await storage.DownloadAsync(result.FileId, async (stream, token) =>
{
    downloaded = await stream.ReadAllBytesAsync(token);
}, ct);
```

### Download to Disk

```csharp
await using var destination = new FileStream(@"C:\output\downloaded.pdf", FileMode.Create);
await storage.DownloadAsync(result.FileId, async (source, token) =>
    await source.CopyToAsync(destination, 1024 * 1024, token),  // 1 MB buffer
    ct);
```

### Get Metadata

```csharp
var metadata = await storage.GetMetadataAsync(result.FileId, ct);
if (metadata != null)
{
    Console.WriteLine($"Basket: {metadata.Basket}");       // uploads
    Console.WriteLine($"Tenant: {metadata.TenantId}");      // 42
    Console.WriteLine($"Name: {metadata.FileName}");        // document.pdf
    Console.WriteLine($"Type: {metadata.StorageType}");     // s3
}
```

### Delete

```csharp
bool deleted = await storage.DeleteAsync(result.FileId, ct);
// Always returns true if CanProcess(fileId) == true — S3 deletes silently ignore missing objects
```

---

## Settings Reference

### S3FileStorageOptions

| Property | Description | Default |
|----------|-------------|---------|
| `Endpoint` | S3-compatible endpoint URL | *(required)* |
| `AccessKey` | Access key ID | *(required)* |
| `SecretKey` | Secret access key | *(required)* |
| `Bucket` | Target bucket name | *(required)* |
| `Basket` | Scope/container prefix within the bucket | `"share"` |
| `Region` | AWS region for SigV4 signing | `"eu-central-1"` |
| `StorageType` | Scheme prefix in File ID | `"s3"` |
| `IsReadOnly` | Prevent write/delete operations | `false` |
| `TotalRequestTimeout` | Per-request timeout | `180 sec` |
| `ConnectionPoolLifetime` | Connection pool lifetime | `15 min` |
| `HandlerLifetime` | HttpClient handler lifetime | `∞` (infinite) |
| `Service`, `UseHttp2` | SigV4 service name / force HTTP/2 | `"s3"` / `false` |

`S3FileStorageOptions` is a single mutable type served by the options pipeline, and it inherits
the connection and transport settings from `S3BucketClientSetupOptions` — the very type the bucket
client is constructed from. There is no second copy and no hand-written projection, which is how
`UseHttp2` used to be configurable yet never applied.

### Options are validated

Values are normalised first (`PostConfigure`), then validated. `ValidateOnStart()` turns an
invalid configuration into an `OptionsValidationException` at host start rather than a bare
`UriFormatException` from the middle of the first upload:

| Property | Requirement |
|----------|-------------|
| `Endpoint` | absolute `http(s)` URL, not blank |
| `AccessKey`, `SecretKey`, `Bucket` | not null or blank |
| `StorageType` | at most 10 characters, no `:`, `/` or `\` (it becomes the file ID scheme) |
| `Basket` | 3-63 characters, starts with a letter or `_`, no path separator |
| `TotalRequestTimeout`, `ConnectionPoolLifetime` | positive |
| `HandlerLifetime` | positive, or `Timeout.InfiniteTimeSpan` |

The same checks stay usable from a non-DI path:

```csharp
options.Validate(); // throws DataAnnotations.ValidationException naming the offending option
```

### One registration per service collection

A second `AddSaS3FileStorage` call throws `InvalidOperationException`. The provider owns the
unnamed options instance: a second call would register another `IFileStorage` over it, and both
`Configure` callbacks would apply, so the storage would silently use merged settings.

Rotate credentials or retarget the bucket by editing the one registration, not by adding another.

---


## Dependencies

| Package | Purpose |
|---------|---------|
| `Sa.Data.S3` | S3 client (`IS3BucketClient`, `S3BucketClient`) |
| `Sa` | Shared utilities (`MimeTypeMap`) |

The `AddSaS3FileStorage` extension method automatically registers `IS3BucketClient` via `AddSaS3BucketClient`.

---

## License

MIT
