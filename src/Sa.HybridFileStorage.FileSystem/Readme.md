# Sa.HybridFileStorage.FileSystem

Local filesystem provider for `Sa.HybridFileStorage`. Stores files as physical files on disk, delegating every read, write and delete to a `Sa.Data.TempFolder` instance that owns the root, the path guard, the cleanup activity and the retry policy.

---

## Table of Contents

- [Overview](#overview)
- [File ID Format](#file-id-format)
- [Installation](#installation)
- [Quick Start](#quick-start)
  - [Pre-initialisation (Configure)](#pre-initialisation-configure)
  - [Post-initialisation (PostConfigure)](#post-initialisation-postconfigure)
  - [From configuration](#from-configuration)
  - [Several storages in one host](#several-storages-in-one-host)
- [CRUD Examples](#crud-examples)
- [Options Reference](#options-reference)
- [Security](#security)
- [Error Handling](#error-handling)
- [Migrating from `BasePath`](#migrating-from-basepath)

---

## Overview

The filesystem provider registers an `IFileStorage` backed by the local file system. Files are stored under the root of a keyed `Sa.Data.TempFolder` instance using the structure:

```
{RootPath}/{Basket}/{TenantId}/{FileName}
```

The storage itself only maps a File ID to that relative path and shapes the result. The root, the path guard, the cleanup activity marker and the transient-I/O retries are the temp folder's — the provider no longer opens `FileStream`s or calls `File.Delete` directly.

Key characteristics:
- **Path guarding** — every path is resolved against the temp-folder root; traversal and injection-style characters are rejected
- **Cleanup lifetime** — files live under a temp-folder instance with an age-based cleanup (30 days by default; see below)
- **Preallocated writes** — the temp folder preallocates the target file when the source length is known
- **Retries** — transient `IOException` / `UnauthorizedAccessException` failures are retried by the temp folder

---

## File ID Format

```
fs://{basket}/{tenantId}/{fileName}
```

**Examples:**
- `fs://documents/42/report.pdf`
- `fs://uploads/7/avatar.png`
- `fs://share/100/data.bin`

> Note: slashes and backslashes in `FileName` are sanitized to the platform separator and stripped of leading separators.

---

## Installation

```powershell
dotnet add package Sa.HybridFileStorage.FileSystem
```

---

## Quick Start

The provider is registered **by name**, because its root and I/O policy live on a temp folder registered under the same name. The callback receives the `IFileSystemStorageBuilder`: the options section comes from `FromConfiguration("…")`, the standard `Configure` / `PostConfigure` / `Validate` methods are reached through `Options(...)`, and the **mandatory** root/I-O channel is `TempFolder(...)`:

```csharp
using Sa.HybridFileStorage.FileSystem;

builder.Services.AddSaFileSystemFileStorage("documents", b => b
    .Options(ob => ob.Configure(options => options.Basket = "documents"))
    .TempFolder(tb => tb.Options(ob => ob.Configure(folder =>
    {
        folder.RootPath = @"C:\data\files";
    }))));
```

A storage without the `TempFolder(...)` channel throws `InvalidOperationException` at the
`AddSaFileSystemFileStorage` call — a filesystem storage with no root has nowhere to put a file.

The storage options pipeline runs in a fixed order — **`Configure` → `PostConfigure` → validate** — so a value normalised in post-initialisation is what validation sees.

### Pre-initialisation (Configure)

`Configure` runs first and receives the raw values:

```csharp
builder.Services.AddSaFileSystemFileStorage("documents", b => b
    .Options(ob => ob.Configure(options => options.Basket = "documents"))
    .TempFolder(tb => tb.Options(ob => ob.Configure(folder => folder.RootPath = @"C:\data\files"))));
```

### Post-initialisation (PostConfigure)

`PostConfigure` runs after every `Configure` and before validation. The registration already trims `StorageType` / `Basket`; anything you add here runs after that, so it sees normalised values:

```csharp
builder.Services.AddSaFileSystemFileStorage("documents", b => b
    .Options(ob => ob.Configure(options => options.Basket = "documents")
        .PostConfigure(options => options.IsReadOnly = false))
    .TempFolder(tb => tb.Options(ob => ob.Configure(folder => folder.RootPath = @"C:\data\files"))));
```

### From configuration

Pass the sections via `FromConfiguration`; the storage options and the temp-folder options bind from `IConfiguration`:

```csharp
// appsettings.json
// {
//   "FileSystemStorage": { "Basket": "documents" },
//   "TempFolder":        { "RootPath": "C:\\data\\files", "MaxAge": "30.00:00:00" }
// }

builder.Services.AddSaFileSystemFileStorage("documents", b => b
    .FromConfiguration("FileSystemStorage")
    .TempFolder(tb => tb.FromConfiguration("TempFolder")));
```

The section binds in a fixed slot **before** the `Options(...)` actions replay, so their `Configure` has the last word when both are used.

### Several storages in one host

Each call registers one storage under its own name; different names are independent, so one host can
run several filesystem roots. Repeating a name throws `InvalidOperationException` — the name keys
both the storage and its temp folder (`sp.GetRequiredKeyedService<ITempFolder>(name)`).

A blank or missing `RootPath`, or one pointing at the system temp directory, surfaces as
`OptionsValidationException` — at host start (`ValidateOnStart()`) or at first resolve in a container
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

// File created at: {RootPath}/documents/1/hello.txt
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

### Delete

```csharp
var deleted = await storage.DeleteAsync(result.FileId, ct);
// true  — a file existed and was removed
// false — no file there, or a transient failure survived the temp folder's retries
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
copy step that could drop a property. The storage root and the file-I/O policy now live on
`TempFolderOptions`, reached through the mandatory `TempFolder(...)` channel.

| Property | Description | Default |
|----------|-------------|---------|
| `Basket` | Container name appended to the root | `"share"` |
| `StorageType` | Scheme prefix in File ID | `"fs"` |
| `IsReadOnly` | Prevent write/delete operations | `false` |

`FileSystemStorageOptions.DefaultStorageType` and `FileSystemStorageOptions.DefaultBasket` are
exposed as constants. The defaults live on the type itself, so binding a partial configuration
leaves the rest intact.

### TempFolderOptions (the storage root)

The `TempFolder(...)` channel configures the keyed `ITempFolder` the storage reads and writes
through. The properties that matter to a filesystem storage:

| Property | Description | Default |
|----------|-------------|---------|
| `RootPath` | Root directory of the storage; **must not** be blank or the system temp directory | `Path.GetTempPath()` (rejected) |
| `MaxAge` | Age after which the cleanup strategy removes an expired subfolder | **30 days** (storage default) |
| `TouchDebounce` | Debounce before a write refreshes the folder's activity marker | 5 seconds |
| `OverwriteFiles` | Whether an existing file is replaced on write | `true` |

`Sa.HybridFileStorage.FileSystem` raises a storage's `MaxAge` from the temp-folder default of 24
hours to **30 days**, because storage files are meant to outlive a scratch folder. A section value
or a code `Configure` that sets `MaxAge` still wins.

Validation runs after post-configuration and enforces:

| Requirement | Applies to |
|-------------|------------|
| `StorageType` at most 10 characters, no `:`, `/` or `\` | `StorageType` |
| `Basket` 3-63 characters, starts with a letter or `_`, no path separator | `Basket` |
| `RootPath` set, outside the system temp directory | `TempFolderOptions` |

### Adding your own validation

```csharp
builder.Services.AddSaFileSystemFileStorage("documents", b => b
    .Options(ob => ob.Configure(options => options.Basket = "documents")
        .Validate(options => options.StorageType == "fs", "Only the 'fs' scheme is supported."))
    .TempFolder(tb => tb.Options(ob => ob.Configure(folder => folder.RootPath = @"C:\data\files"))));
```

Your rule runs in addition to the built-in checks; all failures are reported together in the
resulting `OptionsValidationException`.

---

## Security

The provider protects against directory traversal and injection attacks through the temp folder's
path guard:

1. **Path sanitisation** — leading `/` or `\` characters in `FileName` are stripped; all separators are converted to the platform's directory separator
2. **Root containment** — every resolved file path must land inside `{RootPath}/{Basket}`. Attempts to escape via `..` are rejected with `SecurityException`
3. **Injection rejection** — `~`, shell/glob metacharacters (`< > | & ; ` $ ^ * ? " ' %`), control and invisible Unicode characters are rejected with `SecurityException`
4. **Deterministic paths** — File IDs map to relative paths without resolution, preventing symlink-based attacks

```csharp
// Safe — normalised to "report.pdf"
new UploadFileInput { FileName = "/api/files/download/file/var/www/report.pdf" }
// Creates: {RootPath}/documents/1/report.pdf

// Blocked — directory traversal detected
// fileName = "../../../etc/passwd" → throws SecurityException

// Blocked — injection-style character (query-string percent)
// fileName = "file%name.txt" → throws SecurityException
```

---

## Error Handling

| Scenario | Behavior |
|----------|----------|
| `IsReadOnly = true` + upload/delete | Throws `HybridFileStorageWritableException` |
| File not found during download/delete | Returns `false` (no exception) |
| Path points at a directory during download/delete | Throws `InvalidOperationException` |
| IOException on delete | Retried by the temp folder; returns `false` if all retries fail |
| Path escape or injection attempt | Throws `SecurityException` |
| Invalid File ID format | Throws `ArgumentException` |
| Invalid options | Throws `OptionsValidationException` at host start (`ValidateOnStart`) or on first resolve |

---

## Migrating from `BasePath`

`FileSystemStorageOptions.BasePath` was removed: the root now lives on the temp folder behind the
mandatory `TempFolder(...)` channel. To keep existing File IDs valid, point the new root at the old
value:

```csharp
// Before
builder.Services.AddSaFileSystemFileStorage(o => o.Options(ob => ob.Configure(x =>
{
    x.BasePath = @"C:\data\files";
    x.Basket = "documents";
})));

// After
builder.Services.AddSaFileSystemFileStorage("documents", b => b
    .Options(ob => ob.Configure(x => x.Basket = "documents"))
    .TempFolder(tb => tb.Options(ob => ob.Configure(folder =>
        folder.RootPath = @"C:\data\files"))));
```

`FileSystemStorageOptions.BufferSize` was also removed — the temp folder owns the copy buffer
(81920 bytes). The registration is now named-only: every existing
`AddSaFileSystemFileStorage(configure)` call needs a name and a `TempFolder(...)` channel.

---

## License

MIT
