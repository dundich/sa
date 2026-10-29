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
- `fs://share/7/sub/dir/file.txt` — a nested `FileName` keeps its directories

> Note: leading separators in `FileName` are stripped, backslashes are treated as separators, and a
> File ID always uses `/` — nested names read the same on every OS. On disk the layout mirrors the
> ID (`{RootPath}/{basket}/{tenantId}/{fileName}`).

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
run several filesystem roots. Repeating a name throws `InvalidOperationException` — the name keys the
storage, its named options instance and its temp folder:

```csharp
var documents = sp.GetRequiredKeyedService<IFileStorage>("documents");
var uploads   = sp.GetRequiredKeyedService<IFileStorage>("uploads");
```

The unkeyed `IFileStorage` registration stays as an alias of the last registered storage, so
`sp.GetRequiredService<IFileStorage>()` keeps working for the single-storage case (and for
`IHybridFileStorage`, which enumerates the unkeyed registrations only).

A blank or missing `RootPath`, or one pointing at the system temp directory, surfaces as
`OptionsValidationException` — at host start (`ValidateOnStart()`) or at first resolve in a container
built by hand — rather than on the first upload.

---

## CRUD Examples

`storage` below is the registered `IFileStorage`; `hybridStorage` is `IHybridFileStorage` from the
core package.

```csharp
var storage = sp.GetRequiredService<IFileStorage>();
// several storages in one host: sp.GetRequiredKeyedService<IFileStorage>("documents")
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

`GetMetadataAsync` parses and shapes the File ID, like the S3 and Postgres providers: it answers for
any well-formed ID this storage owns without probing the disk (only the in-memory provider checks
that the entry exists). `DownloadAsync` / `DeleteAsync` are the calls that report a missing file, and
`Basket` in the result is always the configured basket.

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
| `RootPath` | Root directory of the storage; **must not** be blank or the system temp directory itself (a subdirectory of it is accepted but the OS may wipe it) | `Path.GetTempPath()` (rejected) |
| `MaxAge` | Age after which the cleanup strategy removes an expired subfolder | **30 days** (storage default) |
| `CleanupEnabled` | Whether the storage's temp folder deletes expired subfolders at all | `true` |
| `OverwriteFiles` | Whether an existing file is replaced on write | `true` |
| `ReadOnly` | Rejects every mutation in the temp folder. Independent of `FileSystemStorageOptions.IsReadOnly` — see below | `false` |
| `TouchDebounce` | Debounce before a write refreshes the folder's activity marker | 5 seconds |

`FileSystemStorageOptions.IsReadOnly` and `TempFolderOptions.ReadOnly` are two switches for one
intent: the storage reports `IsReadOnly` to `HybridFileStorage` (which skips it when choosing a
target for a write), while the temp folder enforces it on the disk. Set both when a storage must
not change — with only the storage flag set, `HybridFileStorage` skips the storage but its files are
still cleaned up by age; with only the temp-folder flag set, the hybrid chain still picks the
storage for a write and the write then fails with `InvalidOperationException`.

`Sa.HybridFileStorage.FileSystem` raises a storage's `MaxAge` from the temp-folder default of 24
hours to **30 days**, because storage files are meant to outlive a scratch folder. A section value
or a code `Configure` that sets `MaxAge` still wins.

`CleanupEnabled = false` (through the same `TempFolder(...)` channel) turns the deletion off: the
storage keeps working, but nothing is ever cleaned up — use it when the files' lifetime is managed
elsewhere. The volume tracking and the free-space check keep running: they only report.

Validation runs after post-configuration and enforces:

| Requirement | Applies to |
|-------------|------------|
| `StorageType` at most 10 characters, no `:`, `/` or `\` | `StorageType` |
| `Basket` 3-63 characters, starts with a letter or `_`, no path separator | `Basket` |
| `RootPath` set and not the system temp directory itself | `TempFolderOptions` |

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

1. **Path sanitisation** — leading `/` or `\` characters in `FileName` are stripped; separators are normalised, so a nested name is a set of directories under the tenant
2. **Root containment** — every resolved file path must land inside `{RootPath}/{Basket}` (a sibling basket that only shares the name prefix does not belong to this storage). Attempts to escape via `..` are rejected with `SecurityException`
3. **Injection rejection** — `~`, shell/glob metacharacters (`< > | & ; ` $ ^ * ? " ' %`), control and invisible Unicode characters are rejected with `SecurityException`
4. **Portable names** — reserved Windows device names (`CON`, `PRN`, `AUX`, `NUL`, `CONIN$`, `CONOUT$`, `COM0`–`COM9`, `LPT0`–`LPT9`, with or without an extension) and segments ending with a dot are rejected with `SecurityException`, on every platform: Windows would resolve the former to a device and alias the latter to another file, so `report.txt.` would silently share a file with `report.txt`
5. **Deterministic paths** — File IDs map to relative paths without resolution, preventing symlink-based attacks

```csharp
// Safe — leading separators are dropped, the directories below them are kept
new UploadFileInput { FileName = "/api/files/var/www/report.pdf" }
// Creates: {RootPath}/documents/1/api/files/var/www/report.pdf
// File ID: fs://documents/1/api/files/var/www/report.pdf

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
| `TempFolderOptions.ReadOnly = true` + upload/delete | Throws `InvalidOperationException` (the temp folder owns the switch; set `FileSystemStorageOptions.IsReadOnly` too so the hybrid chain skips this storage) |
| File not found during download/delete | Returns `false` (no exception) |
| Path points at a directory during download/delete | Throws `InvalidOperationException` |
| `IOException` / `UnauthorizedAccessException` on delete | Retried by the temp folder; returns `false` if all retries fail |
| Permission failure on upload | Throws `UnauthorizedAccessException` from the temp folder (not retried away) |
| Path escape or injection attempt | Throws `SecurityException` |
| Reserved device name (`NUL`, `COM1`, …) or a segment ending with a dot | Throws `SecurityException` |
| Malformed or foreign File ID | `CanProcess` returns `false`; download/delete return `false`, `GetMetadataAsync` returns `null` |
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
