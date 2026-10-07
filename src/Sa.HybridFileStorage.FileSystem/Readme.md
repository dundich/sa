# Sa.HybridFileStorage.FileSystem

Local filesystem provider for `Sa.HybridFileStorage`. Stores files as physical files on disk with path sanitisation, security checks, and retry logic for transient I/O errors.

---

## Table of Contents

- [Overview](#overview)
- [File ID Format](#file-id-format)
- [Installation](#installation)
- [Quick Start](#quick-start)
  - [Pre-initialisation (Configure)](#pre-initialisation-configure)
  - [Post-initialisation (PostConfigure)](#post-initialisation-postconfigure)
  - [From configuration](#from-configuration)
  - [One storage per collection](#one-storage-per-collection)
- [CRUD Examples](#crud-examples)
- [Options Reference](#options-reference)
- [Security](#security)
- [Error Handling](#error-handling)

---

## Overview

The filesystem provider registers an `IFileStorage` backed by the local file system. Files are stored under a configurable base directory using the structure:

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

The provider is registered through the standard `Microsoft.Extensions.Options` pipeline. It returns
the `IServiceCollection`, so it composes with the other `Add...` calls:

```csharp
using Sa.HybridFileStorage.FileSystem;

builder.Services.AddSaFileSystemFileStorage(o => o.Options(ob => ob.Configure(options =>
{
    options.BasePath = @"C:\data\files";
    options.Basket = "documents";
})));
```

The `configure` callback receives the `IFileSystemStorageBuilder`: the section comes from
`FromConfiguration("…")` and the standard `Configure` / `PostConfigure` / `Validate` methods are
reached through `Options(...)` — there is no bespoke options overload.

The pipeline runs in a fixed order — **`Configure` → `PostConfigure` → validate** — so a value
normalised in post-initialisation is what validation sees.

### Pre-initialisation (Configure)

`Configure` runs first and receives the raw values:

```csharp
builder.Services.AddSaFileSystemFileStorage(o => o.Options(ob => ob.Configure(options =>
{
    options.BasePath = @"C:\data\files";
    options.BufferSize = 512 * 1024;
})));
```

### Post-initialisation (PostConfigure)

`PostConfigure` runs after every `Configure` and before validation. The registration already
normalises `BasePath` to a full path and trims `StorageType` / `Basket`; anything you add here
runs after that, so it sees normalised values:

```csharp
builder.Services.AddSaFileSystemFileStorage(o => o
    .Options(ob => ob.Configure(options => options.BasePath = @"C:\data\files")
    .PostConfigure(options => options.BufferSize = 1024 * 1024)));
```

### From configuration

Pass the section via `FromConfiguration` and the options are bound from `IConfiguration`:

```csharp
// appsettings.json
// { "FileSystemStorage": { "BasePath": "C:\\data\\files", "Basket": "documents" } }

builder.Services.AddSaFileSystemFileStorage(b => b.FromConfiguration("FileSystemStorage"));
```

The section binds in a fixed slot **before** the `Options(...)` actions replay, so their
`Configure` has the last word when both are used.

### One storage per collection

`AddSaFileSystemFileStorage` owns the unnamed `FileSystemStorageOptions` instance, so a second call
throws `InvalidOperationException`. Register the filesystem provider once and route the remaining
baskets to other providers.

Options are validated lazily on first resolve, and `ValidateOnStart()` also forces validation when
the host starts. A blank `BasePath`, a malformed `Basket` or a non-positive `BufferSize` therefore
surfaces as `OptionsValidationException` — at host start, or at the first resolve in a container
built by hand — rather than on the first upload.

---

## CRUD Examples

`storage` below is the registered `IFileStorage`; `hybridStorage` is `IHybridFileStorage` from the
core package.

```csharp
var storage = sp.GetRequiredService<IFileStorage>();
```

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

## Options Reference

### FileSystemStorageOptions

One mutable type, served by the options pipeline. Every property is bindable and settable, and the
same instance is what the storage is constructed from — there is no second settings type and no
copy step that could drop a property.

| Property | Description | Default |
|----------|-------------|---------|
| `BasePath` | Root directory for all files | *(required)* |
| `Basket` | Container name appended to `BasePath` | `"share"` |
| `StorageType` | Scheme prefix in File ID | `"fs"` |
| `IsReadOnly` | Prevent write/delete operations | `false` |
| `BufferSize` | Read/write buffer size in bytes | `262144` (256 KB) |

`FileSystemStorageOptions.DefaultStorageType` and `FileSystemStorageOptions.DefaultBasket` are
exposed as constants. The defaults live on the type itself, so binding a partial configuration
leaves the rest intact.

Validation runs after post-configuration and enforces:

| Requirement | Applies to |
|-------------|------------|
| `BasePath` not null or blank | `BasePath` |
| `BasePath` is an absolute, creatable path | `BasePath` |
| `StorageType` at most 10 characters, no `:`, `/` or `\` | `StorageType` |
| `Basket` 3-63 characters, starts with a letter or `_`, no path separator | `Basket` |
| `BufferSize` greater than zero | `BufferSize` |

A blank `BasePath` is deliberately **not** normalised in post-configuration: `Path.GetFullPath("   ")`
succeeds on Unix and would silently create a directory named `"   "`.

### Adding your own validation

```csharp
builder.Services.AddSaFileSystemFileStorage(o => o
    .Options(ob => ob.Configure(options => options.BasePath = @"C:\data\files")
    .Validate(options => options.BufferSize >= 64 * 1024, "BufferSize must be at least 64 KB.")));
```

Your rule runs in addition to the built-in checks; all failures are reported together in the
resulting `OptionsValidationException`.

---

## Security

The provider protects against directory traversal attacks:

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
| Invalid options | Throws `OptionsValidationException` at host start (`ValidateOnStart`) or on first resolve |

---

## License

MIT
