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
builder.Services.AddSaFileSystemFileStorage(options =>
{
    options.BasePath = @"C:\data\files";
    options.Basket = "documents";
    options.IsReadOnly = false;
    options.StorageType = "fs";
    options.BufferSize = 256 * 1024;
});
```

The options are validated at registration time, so a blank `BasePath`, a malformed `Basket`
or a non-positive `BufferSize` throws there rather than on the first upload.

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

### FileSystemStorageOptions (immutable record)

Used with the `Action<FileSystemStorageOptions>` overload:

| Property | Description | Default |
|----------|-------------|---------|
| `BasePath` | Root directory for all files | *(required)* |
| `Basket` | Container name | `"share"` |
| `StorageType` | Scheme prefix in File ID | `"fs"` |
| `IsReadOnly` | Prevent write/delete operations | `false` |
| `BufferSize` | Read/write buffer size in bytes | `262144` (256 KB) |

```csharp
builder.Services.AddSaFileSystemFileStorage(options =>
{
    options.BasePath = @"C:\data\files";
    options.Basket = "documents";
    options.BufferSize = 512 * 1024;
});
```

The overload converts the options with `ToSettings()` and delegates to the settings
overload, so there is exactly one place where the two types are mapped. Both overloads
validate eagerly at registration: nothing is added to the service collection if the options
are invalid. `FileSystemStorageSettings` and `FileSystemStorageOptions` are both immutable
records, and the registration copies them, so a later edit to your instance does not
affect the registered storage.

| Requirement | Applies to |
|-------------|------------|
| `BasePath` not null or blank | both |
| `BasePath` is an absolute, creatable path | both |
| `StorageType` at most 10 characters, no `:`, `/` or `\` | both |
| `Basket` 3-63 characters, starts with a letter or `_`, no path separator | both |
| no path separator inside `BasePath` beyond the platform root | both |

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
| Invalid options at registration | Throws `ArgumentException` before anything is registered |

---

## Breaking changes

### 0.12.0 -> 0.13.0

**`AddSaFileSystemFileStorage(Action<IServiceProvider, FileSystemStorageOptions>)` was
removed.** It built a throwaway `new ServiceCollection().BuildServiceProvider()` to
hand a provider to the callback, so the callback could not resolve `IConfiguration`,
`IHostEnvironment` or any application service. No shim was left behind. Replace it with:

```csharp
// before
builder.Services.AddSaFileSystemFileStorage((sp, options) =>
{
    options.BasePath = sp.GetRequiredService<IHostEnvironment>().ContentRootPath;
});

// after
builder.Services.AddSaFileSystemFileStorage(options =>
{
    options.BasePath = builder.Environment.ContentRootPath;
});
```

**Options are validated at registration**, not at first use. An invalid `BasePath`,
`StorageType` or `Basket` now throws `ArgumentException` from the `Add...` call rather
than on the first upload.

**`FileSystemStorageOptions` and `FileSystemStorageSettings` are immutable records** and
`BufferSize` is present on both. The old mutable builder type copied four of five
properties into settings, dropping `BufferSize`.

---

## License

MIT
