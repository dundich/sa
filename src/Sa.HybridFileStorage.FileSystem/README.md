# Sa.HybridFileStorage.FileSystem

Local filesystem provider for `Sa.HybridFileStorage`. Stores files as physical files on disk with path sanitisation, security checks, and retry logic for transient I/O errors.

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
- [Security](#security)
- [Error Handling](#error-handling)

---

## Overview

`FileSystemStorage` implements `IFileStorage` backed by the local file system. Files are stored under a configurable base directory using the structure:

```
{BasePath}/{Basket}/{TenantId}/{FileName}
```

Key characteristics:
- **Path sanitisation** — prevents directory traversal attacks
- **Smart preallocation** — uses `FileStreamOptions.PreallocationSize` when stream length is known
- **Retry helper** — retries `IOException` on delete operations
- **Streaming reads/writes** — configurable buffer size for memory efficiency

---

## File ID Format

```
fs://{basket}/{tenantId}/{fileName}
```

**Examples:**
- `fs://documents/42/report.pdf`
- `fs://uploads/7/avatar.png`
- `fs://share/100/data.bin`

> Note: slashes and backslashes in `FileName` are sanitized to forward slashes and stripped of leading separators.

---

## Installation

```powershell
dotnet add package Sa.HybridFileStorage.FileSystem
```

---

## Quick Start

### Without DI

```csharp
using Sa.HybridFileStorage.FileSystem;
using Sa.HybridFileStorage.Domain;

var settings = new FileSystemStorageSettings
{
    BasePath = @"C:\data\files",
    Basket = "documents"
};

using var storage = new FileSystemStorage(settings);

// Upload
using var stream = File.OpenRead(@"C:\temp\document.pdf");
var result = await storage.UploadAsync(
    new UploadFileInput { FileName = "document.pdf", TenantId = 42 },
    stream, ct);

Console.WriteLine(result.FileId);  // fs://documents/42/document.pdf

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

```csharp
using Sa.HybridFileStorage.FileSystem;

// Option 1: Immutable settings (recommended)
builder.Services.AddSaFileSystemFileStorage(new FileSystemStorageSettings
{
    BasePath = @"C:\data\files",
    Basket = "documents"
});

// Option 2: Mutable options with fluent builder
builder.Services.AddSaFileSystemFileStorage((sp, options) =>
{
    options.BasePath = @"C:\data\files";
    options.Basket = "documents";
    options.IsReadOnly = false;
    options.StorageType = "fs";
});
```

---

## CRUD Examples

### Upload from Stream

```csharp
using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Hello, world!"));
var result = await storage.UploadAsync(
    new UploadFileInput { FileName = "hello.txt", TenantId = 1 },
    stream, ct);

// File created at: {BasePath}/documents/1/hello.txt
// File ID: fs://documents/1/hello.txt
```

### Upload from File

Use `CopyFromFileAsync` from `Sa.HybridFileStorage` core:

```csharp
var result = await hybridStorage.CopyFromFileAsync(
    filePath: @"C:\temp\large-video.mp4",
    basket: "media",
    input: new UploadFileInput { FileName = "video.mp4", TenantId = 5 },
    bufferSize: 1024 * 1024,  // 1 MB buffer for large files
    ct: ct);
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
using var destination = new FileStream(@"C:\output\downloaded.pdf", FileMode.Create);
await storage.DownloadAsync(result.FileId, async (source, token) =>
    await source.CopyToAsync(destination, 81920, token),
    ct);
```

### Get Metadata

```csharp
var metadata = await storage.GetMetadataAsync(result.FileId, ct);
if (metadata != null)
{
    Console.WriteLine($"Basket: {metadata.Basket}");
    Console.WriteLine($"Tenant: {metadata.TenantId}");
    Console.WriteLine($"Name: {metadata.FileName}");
    Console.WriteLine($"Type: {metadata.StorageType}");
}
```

---

## Settings Reference

### FileSystemStorageSettings (immutable)

| Property | Description | Default |
|----------|-------------|---------|
| `BasePath` | Root directory for all files | *(required)* |
| `Basket` | Container name appended to BasePath | `"share"` |
| `StorageType` | Scheme prefix in File ID | `"fs"` |
| `IsReadOnly` | Prevent write/delete operations | `false` |
| `BufferSize` | Read/write buffer size in bytes | `262144` (256 KB) |

### FileSystemStorageOptions (mutable, fluent builder)

Used with the `Action<IServiceProvider, FileSystemStorageOptions>` overload:

| Property | Description | Default |
|----------|-------------|---------|
| `BasePath` | Root directory for all files | *(required)* |
| `Basket` | Container name | `"share"` |
| `StorageType` | Scheme prefix in File ID | `"fs"` |
| `IsReadOnly` | Prevent write/delete operations | `false` |

Call `options.Validate()` after configuration to enforce required fields.

---

## Security

`FileSystemStorage` protects against directory traversal attacks:

1. **Path sanitisation** — leading `/` or `\` characters in `FileName` are stripped; all backslashes are converted to forward slashes
2. **Base path containment** — every resolved file path is checked for belonging to `{BasePath}/{Basket}`. Attempts to escape via `../` are rejected with `SecurityException`
3. **Deterministic paths** — File IDs map to relative paths without resolution, preventing symlink-based attacks

```csharp
// Safe — normalised to "report.pdf"
new UploadFileInput { FileName = "/api/files/download/file/var/www/report.pdf" }
// Creates: {BasePath}/documents/1/report.pdf

// Blocked — directory traversal detected
// fileName = "../../../etc/passwd" → throws SecurityException
```

---

## Error Handling

| Scenario | Behavior |
|----------|----------|
| `IsReadOnly = true` + upload/delete | Throws `HybridFileStorageWritableException` |
| File not found during download/delete | Returns `false` (no exception) |
| IOException on delete | Retries internally; returns `false` if all retries fail |
| Path escape attempt | Throws `SecurityException` |
| Invalid File ID format | Throws `ArgumentException` |

---

## License

MIT
