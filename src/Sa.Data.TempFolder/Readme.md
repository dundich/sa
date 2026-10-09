# Sa.Data.TempFolder

Temporary directory management: path-guarded subfolders under a root, debounced activity marking of written folders, age-based background cleanup, low-priority volume tracking with an edge-triggered limit event, and optional free-space watching. Native AOT-safe, keyed multi-instance registration.

---

## Table of Contents

- [Overview](#overview)
- [Installation](#installation)
- [Quick Start](#quick-start)
  - [From configuration](#from-configuration)
  - [Several instances](#several-instances)
  - [Code overrides](#code-overrides)
- [API](#api)
- [Path rules](#path-rules)
- [Cleanup](#cleanup)
- [Debounced activity marking](#debounced-activity-marking)
- [Volume tracking](#volume-tracking)
- [Free space](#free-space)
- [Background service](#background-service)
- [Read-only mode](#read-only-mode)
- [Options Reference](#options-reference)
- [Error Handling](#error-handling)
- [FilePathResolver](#filepathresolver)

---

## Overview

`AddSaTempFolder` registers one (or several) keyed `ITempFolder` instances — a root directory plus the strategies and policies configured for it:

- **Path guard** — every path argument resolves through `PathGuard`: it may be relative or absolute, but the result must stay inside the root; `..` segments and injection-style characters (`~`, `>`, shell metacharacters, control/invisible Unicode) are rejected (`SecurityException`).
- **Age-based cleanup** — an expired top-level subfolder goes wholesale; a fresh one is descended into, and the same rule applies at every inner level, so date hierarchies (`yyyy/MM/dd/HH` …) shed their old day/hour folders while the year/month containers stay (prefix-filtered at the top level, budget-capped, oldest first, `Retry.Linear` on transient I/O errors). Loose files directly in the root are never touched.
- **Date naming** — `Naming = Date` names folders `{FolderPrefix}+{current time shaped by FolderNameFormat}` (any of `yyyy-MM-dd`, `yyyy-MM-dd/HH`, `yyyy/MM/dd`, `yyyy/MM/dd/HH`, `yyyyMMdd`, `yyyyMMdd/HH`, `yyyyMM/dd`, `yyyyMM/dd/HH` …) — a readable hierarchy the cleanup can age level by level. `GuidV7` stays the default.
- **Debounced activity marking** — a burst of accesses (writes and successful reads) re-arms one timer per touched folder; only a quiet period of `TouchDebounce` actually refreshes the last write time of the accessed file's own directory **and every ancestor up to the root**, so active hierarchies survive cleanup at every level.
- **Cleanup yields to reads, writes and deletes** — an in-flight `WriteAsync` / `CopyFileAsync` / `ReadAsync` / `DeleteFileAsync` / `CreateSubfolder` cancels the cleanup pass: a pass never starts while an operation runs (it returns 0), one already running when an operation arrives ends by cancellation at its next checkpoint, and a folder whose debounced marker touch has not landed yet is skipped — a just-accessed file is never deleted.
- **Overwrite policy** — `WriteAsync` / `CopyFileAsync` replace an existing target by default; set `OverwriteFiles = false` to fail the write with `IOException` instead (the original stays intact).
- **Volume tracking** — an optional low-priority background scan (`ThreadPriority.BelowNormal`) measures the whole root, caches per-folder sizes keyed by the folder's last write time, and fires `OnVolumeExceeded` **edge-triggered** (once over the limit, once back under). The event only notifies — nothing is deleted because of the volume itself.
- **Free-space watching** — off by default; with `MinFreeSpace > 0` the service polls the free space of the root's volume (first check at host start, then every `FreeSpaceCheckInterval`, 24 h by default) and fires `OnFreeSpaceReached` **edge-triggered** when the reading reaches the limit.
- **Fail-fast startup** — the background service access-checks every root (create / list / write probe) before the loops start.

---

## Installation

```powershell
dotnet add package Sa.Data.TempFolder
```

---

## Quick Start

```csharp
using Sa.Data.TempFolder;

builder.Services.AddSaTempFolder("uploads", b => b.Options(ob => ob.Configure(options =>
{
    options.RootPath = "/var/tmp/uploads";
    options.MaxAge = TimeSpan.FromHours(6);
    options.FolderPrefix = "up_";
})));
```

Resolve it as a keyed service (a bare `GetRequiredService<ITempFolder>()` does **not** resolve keyed services):

```csharp
var temp = sp.GetRequiredKeyedService<ITempFolder>("uploads");

var jobDir = temp.CreateSubfolder("jobs/42");
var (relative, absolute) = await temp.WriteAsync(stream, "jobs/42/payload.bin", ct);
var found = await temp.ReadAsync("jobs/42/payload.bin", async (s, ct) => await s.CopyToAsync(output, ct), ct);
var removedFile = await temp.DeleteFileAsync("jobs/42/payload.bin", ct);
await foreach (var file in temp.EnumerateFilesAsync("*.part", "jobs", recursive: true, ct)) { … }
var removed = await temp.CleanupExpiredAsync(ct);
```

The `configure` callback receives the `ITempFolderBuilder`: the section comes from `FromConfiguration("…")`, lowest-precedence per-registration defaults from `Defaults(…)`, and the standard `Configure` / `PostConfigure` / `Validate` methods are reached through `Options(...)` — there is no bespoke options overload.

### From configuration

```json
{
  "TempFolder": {
    "RootPath": "/var/tmp/uploads",
    "MaxAge": "06:00:00",
    "FolderPrefix": "up_",
    "CleanupInterval": "01:00:00",
    "TrackVolume": true,
    "MaxTotalSize": 10737418240
  }
}
```

```csharp
builder.Services.AddSaTempFolder("uploads", b => b.FromConfiguration("TempFolder"));
```

Scalar values bind from the section; `OnVolumeExceeded` and `OnFreeSpaceReached` are code-only channels and are never produced by configuration binding. Section binding runs first — after the `Defaults(...)` channel — so a `Configure` inside `Options(...)` always wins over configuration.

### Defaults

A component that registers a temp folder on the caller's behalf can seed a property on the lowest-precedence channel with `Defaults(...)`; both a value from the section and a code `Configure` override it, so a default never steals an explicit setting:

```csharp
builder.Services.AddSaTempFolder("uploads", b => b
    .Defaults(o => o.MaxAge = TimeSpan.FromDays(30))  // applied only when nothing else sets MaxAge
    .FromConfiguration("TempFolder"));
```

### Several instances

```csharp
builder.Services.AddSaTempFolder("uploads", b => b.FromConfiguration("TempFolder:Uploads"));
builder.Services.AddSaTempFolder("exports", b => b.Options(ob => ob.Configure(o => o.MaxAge = TimeSpan.FromMinutes(30))));

var uploads = sp.GetRequiredKeyedService<ITempFolder>("uploads");
var exports = sp.GetRequiredKeyedService<ITempFolder>("exports");
```

Each call gets its own named options instance (sections and `Configure` callbacks never stack across registrations). Registering the same name twice throws at registration time.

### Code overrides

```csharp
builder.Services.AddSaTempFolder("special", b => b
    .UseCleanupStrategy<EverythingExpiredStrategy>()  // wins over the Cleanup enum
    .UseNamingStrategy<JobFolderNameStrategy>());     // wins over the Naming enum
```

The overridden type is registered as a singleton (its dependencies resolve from the container) and mapped onto this instance's keyed strategy slot. Built-ins are selected by the `Cleanup` / `Naming` enums: `AgeBased` + `GuidV7` by default, or `AgeBased` + `Date` (with `FolderNameFormat`) when configured:

```csharp
builder.Services.AddSaTempFolder("byday", b => b.Options(ob => ob.Configure(o =>
{
    o.Naming = TempFolderNamingKind.Date;      // folders like "up_2026/10/08/14"
    o.FolderNameFormat = "yyyy/MM/dd/HH";      // any .NET date/time format
    o.FolderPrefix = "up_";                    // lands on the first segment
})));
```

The format is rendered with the invariant culture for the container's `TimeProvider` (local time), and its rendered shape is validated along with the prefix — a broken or injection-style format fails with `OptionsValidationException` at resolve/start.

---

## API

| Member | Description |
|---|---|
| `Name` / `RootPath` | Registration key and the fully qualified root. |
| `CreateSubfolder(rel?)` | Creates a subfolder (parents included) — `rel` relative or absolute (inside the root). Without an argument the name comes from the naming strategy: `GuidV7` → `{FolderPrefix}{guid-v7:N}` (time-sortable, collision-free); `Date` → `{FolderPrefix}{FolderNameFormat}` (calls within one format bucket share the folder). |
| `WriteAsync(stream, path, ct)` | Writes a stream into a file — `path` relative or absolute (inside the root) — creating parents, and (debounced) activates the touched folder chain. Overwrites an existing file unless `OverwriteFiles = false`. When the source's remaining length is known (`CanSeek`), the target is preallocated to exactly that size. Returns `(RelativePath, AbsolutePath)`. |
| `CopyFileAsync(src, subfolder?, ct)` | Copies an external file in — `subfolder` relative or absolute (inside the root); without a subfolder a fresh strategy-named one is built and the file keeps its own name (the name itself must pass the [path rules](#path-rules)). Same overwrite policy as `WriteAsync`. |
| `ReadAsync(path, loadStream, ct)` | Reads a file through the callback — `path` relative or absolute (inside the root). Returns `true` when a file was found and handed to `loadStream` (`false` when there is not — the callback is never invoked). The whole read is one activity against cleanup: a concurrent pass is cancelled instead of reading over a deletion. A successful read also refreshes (debounced) the activity markers of the file's directory chain, exactly like a write — a folder that is only ever read from does not age out. Allowed on a read-only instance. |
| `DeleteFileAsync(path, ct)` | Deletes a single **file** — `path` relative or absolute (inside the root). Folders are never removed: a path that points at a directory throws `InvalidOperationException` (expired subfolders go through `CleanupExpiredAsync`, not here), and the file's subfolder is left in place until the pass ages it out. Returns `true` when a file existed and was removed, `false` when there is none (or it vanished concurrently). One activity against cleanup like the other operations; transient failures are retried (`Retry.Linear`, 3 attempts) and rethrown as `IOException` when exhausted. Read-write instances only. |
| `EnumerateFilesAsync(pattern, subfolder?, recursive?, ct)` | Asynchronously enumerates matching files, returns absolute paths. `subfolder` relative or absolute (inside the root); `pattern` is a pattern, not a path, so `*`/`?` are allowed there. |
| `CleanupExpiredAsync(ct)` | Runs one cleanup pass; returns the number of folders deleted (always 0 for a read-only instance). Yields to reads, writes and deletes: cancelled (0) while one is in flight, interrupted when one starts mid-pass, folders with a pending activity touch skipped. |
| `EnsureAccessAsync(ct)` | Verifies the root: creates it when missing (read-write only), lists it, and runs a write/create/delete probe. |

---

## Path rules

Every path argument (`rel`, `path`, `subfolder`, `path`) goes through `PathGuard` before touching the filesystem:

- **Relative or absolute — both accepted.** An absolute path is used as-is; it must resolve to the root itself or to a location under it, otherwise `SecurityException`. Nothing is ever expanded: `~/…` is rejected, not rewritten to a home directory.
- **`..` is rejected as a segment on either separator, anywhere** — even `jobs/../jobs/42`, which would normalise to a path inside the root. No caller needs it in a temp path.
- **Injection-style characters are rejected** with `SecurityException`, before any normalisation:
  - ASCII control characters `0x00–0x1F` / `0x7F` (NUL truncation, `\t`, `\r`, `\n` …);
  - shell/glob metacharacters `` ~ < > | & ; ` $ ^ * ? " ' % `` — redirection, pipes, command chaining/substitution, variable & environment expansion, quote breaking, globbing, and percent-encoded traversal (`..%2f`);
  - a literal `\` on Unix (a Windows path or a shell escape smuggled into a Unix path);
  - Unicode format/control characters — zero-width space, bidi overrides (U+202E) and friends;
  - on Windows: a colon outside the `C:` drive spec (NTFS alternate data streams).
- **Normalisation still applies afterwards**: `Path.GetFullPath` + a separator-aware containment check, so a sibling directory sharing the root's name prefix (`/var/tmp/uploads_evil` vs root `/var/tmp/uploads`) is never mistaken for a child.
- `FolderPrefix` obeys the same rules (validated at resolve/host start — `OptionsValidationException`). The enumeration `pattern` is exempt: `*`/`?` belong to patterns.

---

## Cleanup

One pass = strategy selection → guarded deletion:

1. The strategy (`AgeBased` by default) selects subfolders whose age exceeds `MaxAge`, oldest first across all levels, capped at `MaxFoldersPerPass`. The age comes from `AgeSource`: `LastWriteTime` (default — refreshed by the debounce on writes) or `CreationTime`.
2. **The descent rule:** an expired **top-level** subfolder (filtered by `FolderPrefix`) goes wholesale with everything inside it; a top-level subfolder that is still fresh is descended into, and the same rule applies at every inner level recursively. That is what makes date hierarchies work — `2026` stays for a year while its long-old months inside are shed, and an expired month takes its days with it without anyone walking them first. The prefix filter applies to top-level folders only (nested folders are the instance's own structure — re-filtering them by prefix would exempt every inner level); a non-prefixed top-level folder is skipped *and not descended into*. The descent stops at depth 16 and skips folders that vanish or turn unreadable mid-pass.
3. The instance re-validates every returned path against the root (a hostile strategy cannot escape), deletes with `Retry.Linear` (3 attempts, transient `IOException` / `UnauthorizedAccessException` only) and counts the result.
4. **The pass yields to file activity.** A `WriteAsync` / `CopyFileAsync` / `ReadAsync` / `DeleteFileAsync` / `CreateSubfolder` in flight (or a pending marker walk) cancels the pass outright — it returns 0 and the next interval retries. A read, write or delete arriving mid-pass cancels the pass token instead: the deletion ends by cancellation at its next checkpoint, and the operation proceeds the moment the pass unwinds. A folder whose debounced touch has not landed yet is skipped explicitly — a file accessed moments ago is never taken together with its folder.

Deleted → `LogInformation`; a folder that survives the retries → `LogWarning`, the pass continues. Files directly in the root are out of scope.

---

## Debounced activity marking

With `AgeSource=LastWriteTime` an actively accessed folder (written to or read from) must not look expired — at **any** level of the hierarchy. After every write or successful read one timer per touched folder (the accessed file's own directory) is armed: further accesses within `TouchDebounce` (default 5 s) re-arm it, and only a quiet period of that length sets `LastWriteTimeUtc` = "now" — for that directory **and every ancestor up to (excluding) the root**, one level at a time. The chain matters because overwrites alone never move a directory's mtime, and because the nested cleanup ages intermediate levels too: a file landing in `up_2026/10/08/14/f.bin` keeps `14`, `08`, `10` and `up_2026` fresh. The instance root itself is never touched. Pending touches are dropped on dispose (the folders simply stay older — the safe direction). The walk itself rides the same exclusion as reads, writes and deletes — a cleanup pass can never overlap it — and the pending entry clears only after the walk has landed; that is exactly what lets cleanup tell "just accessed, spare it" (skip) from "quiet for ages, take it" (delete).

---

## Volume tracking

Off by default: with `TrackVolume=false` the volume is never computed and `MaxTotalSize` is inert. When on (and `MaxTotalSize > 0`):

- The measurement runs on a dedicated background thread at `ThreadPriority.BelowNormal`, so walking a large tree never preempts request work.
- Per-folder sizes are cached by the folder's last write time; only touched or modified folders are re-walked.
- The scan runs **before every cleanup pass**, plus its own interval when `VolumeScanInterval > 0` (default `0` = scan only before cleanup).
- `OnVolumeExceeded` is edge-triggered: it fires once when the total crosses `MaxTotalSize` and once when it falls back under — never repeatedly while the state holds. The transition is also logged (`Warning` / `Information`). Handler exceptions are caught and logged. **Nothing is deleted because of the volume** — cleanup stays age-based; the event is for the subscriber (metrics, admin action, adaptive throttling).

```csharp
options.OnVolumeExceeded = e =>
    metrics.TempExceeded(e.Name, e.TotalSize, e.MaxTotalSize, e.Exceeded);
```

---

## Free space

Off by default: with `MinFreeSpace = 0` (the default) the volume is never probed. When a limit is set, the background service runs one check per instance:

- **The probe is a single volume stat** (`DriveInfo` against the root's volume — on Unix the root path itself, so the reading stays on its actual mount; on Windows the drive root). No tree walk, no thread; a probe failure (volume gone, permission denied) is logged by the loop and retried at the next interval.
- **The schedule** is `FreeSpaceCheckInterval` (24 h by default): the first check runs right at host start — an already-tight volume must surface immediately, not after a day — then once per interval.
- `OnFreeSpaceReached` is edge-triggered like `OnVolumeExceeded`: it fires once when the reading drops to or below `MinFreeSpace` (`Reached = true`) and once when it climbs back above (`Reached = false`) — never repeatedly while the state holds, so a daily poll cannot spam the subscriber. A first reading *above* the limit stays silent (nothing has been reached yet); a first reading already *at* the limit fires at once. Transitions are logged (`Warning` / `Information`); handler exceptions are caught and logged. **Nothing is deleted because of the free space** — the event is for the subscriber (metrics, alerts, draining the folder).

```csharp
builder.Services.AddSaTempFolder("uploads", b => b.Options(ob => ob.Configure(o =>
{
    o.MinFreeSpace = 5L * 1024 * 1024 * 1024; // 5 GiB
    o.OnFreeSpaceReached = e =>
        alerts.TempFreeSpace(e.Name, e.AvailableFreeSpace, e.MinFreeSpace, e.Reached);
})));
```

---

## Background service

One `TempFolderCleanerHost` per service collection walks **all** registrations:

- **At start** — `EnsureAccessAsync` for every instance; a broken root fails host startup with a clear message instead of surfacing at first use.
- **Then** — one cleanup loop per instance (`CleanupInterval`, first pass after one interval) and, when configured, one volume-scan loop (`VolumeScanInterval`, first pass immediately) and one free-space loop (`FreeSpaceCheckInterval`, first pass immediately). Pass failures are logged and never bring the host down; all delays use the container's `TimeProvider`, so tests can virtualise the schedule.

---

## Read-only mode

`ReadOnly=true` puts the instance into read mode:

- `CreateSubfolder` / `WriteAsync` / `CopyFileAsync` / `DeleteFileAsync` throw `InvalidOperationException` — reading (`ReadAsync`) and enumeration keep working, they mutate nothing.
- `CleanupExpiredAsync` returns `0`, and the background service **ignores** the instance entirely — no cleanup, no volume scan, no event, no free-space check.
- Startup validation then checks only that the root exists and can be listed (it is never created).

---

## Options Reference

| Option | Default | Description |
|---|---|---|
| `RootPath` | `Path.GetTempPath()` | Root directory; relative values are resolved to a full path before validation. |
| `MaxAge` | 24 h | Age after which a subfolder is expired (see `AgeSource`). |
| `AgeSource` | `LastWriteTime` | `LastWriteTime` \| `CreationTime`. |
| `FolderPrefix` | `""` | Name prefix for generated folders **and** the cleanup filter (plain name — `.`/`..`, separators and [path-rule](#path-rules) characters rejected). With `Naming = Date` it lands on the first segment of the date path (`up_` + `yyyy/MM/dd` → `up_2026/10/08`). |
| `FolderNameFormat` | `yyyy-MM-dd` | .NET date/time format `Naming = Date` shapes folder names with — invariant culture, container's `TimeProvider`, local time. Works with any pattern (`yyyy-MM-dd/HH`, `yyyy/MM/dd`, `yyyyMMdd`, `yyyyMM/dd/HH` …); the rendered shape is validated with the prefix (broken format, `..`, rooted output → `OptionsValidationException`). |
| `OverwriteFiles` | `true` | `WriteAsync` / `CopyFileAsync` replace an existing target file; `false` → the write fails with `IOException` and the original stays intact. |
| `MaxFoldersPerPass` | 100 | Upper bound of deletions per cleanup pass. |
| `CleanupInterval` | 1 h | Background cleanup period per instance. |
| `VolumeScanInterval` | `0` | Independent volume-scan period; `0` = scan only before a cleanup pass. |
| `MinFreeSpace` | `0` | Minimum free bytes the root's volume should keep; `0` = the check is off and the volume is never probed. Positive → one check per `FreeSpaceCheckInterval`, firing `OnFreeSpaceReached` edge-triggered. |
| `FreeSpaceCheckInterval` | 24 h | Free-space check period; first check at host start. Must be positive; only consulted when `MinFreeSpace > 0`. |
| `ReadOnly` | `false` | See [Read-only mode](#read-only-mode). |
| `TrackVolume` | `false` | Measure and cache folder sizes at all. |
| `MaxTotalSize` | `0` | Volume limit in bytes; `0` = off. Exceeding fires the event only. |
| `TouchDebounce` | 5 s | Quiet period before a file access (write or successful read) refreshes the activity markers of the touched folder chain (the file's own directory and every ancestor up to the root). |
| `Cleanup` | `AgeBased` | Built-in cleanup strategy, overridable with `UseCleanupStrategy<T>()`. |
| `Naming` | `GuidV7` | `GuidV7` \| `Date` (with `FolderNameFormat`), overridable with `UseNamingStrategy<T>()`. |
| `OnVolumeExceeded` | `null` | Code-only edge-triggered volume event; never bound from configuration. |
| `OnFreeSpaceReached` | `null` | Code-only edge-triggered free-space event; never bound from configuration. |

Validation is an explicit `IValidateOptions` (`ValidateOnStart()`), not `ValidateDataAnnotations()` — the latter is `RequiresUnreferencedCode` (IL2026) and would break Native AOT.

---

## Error Handling

| Situation | Behaviour |
|---|---|
| Path resolves outside the root (`..`, sibling-prefix, absolute outside) | `SecurityException`. |
| `..` segment or injection character in a path (`~`, `>`, shell metacharacters, control/invisible Unicode) | `SecurityException` — rejected even when the path would have stayed inside the root. See [Path rules](#path-rules). |
| Absolute path argument that stays inside the root | Accepted like a relative one. |
| Operation on a read-only instance | `InvalidOperationException` for mutations — `ReadAsync` / `EnumerateFilesAsync` stay allowed. |
| Target file exists and `OverwriteFiles = false` | `IOException` from `WriteAsync` / `CopyFileAsync` — the original is left intact. |
| `ReadAsync` with no file at the path | Returns `false` — the callback is never invoked. A path pointing at a directory throws `InvalidOperationException`. |
| `DeleteFileAsync` with no file at the path | Returns `false`. A path pointing at a directory throws `InvalidOperationException`; a transient failure that survives the retries rethrows `IOException`. |
| Invalid options | `OptionsValidationException` at first resolve / host start — never at registration (includes a broken or injection-style `FolderNameFormat`). |
| Broken root at startup | `InvalidOperationException` from `EnsureAccessAsync`, failing host start. |
| Folder fails to delete after retries | `LogWarning`, counted as not deleted, the pass continues. |
| Cleanup pass starts while an operation is in flight | The pass is cancelled before selecting anything — returns 0, the next interval retries. |
| A read, write or delete arrives during a cleanup pass | The pass ends by cancellation at its next checkpoint (partial count returned); the operation proceeds after it unwinds. |
| A folder's debounced marker touch has not landed yet | The pass skips the folder — a just-accessed file is never deleted. |
| Strategy throws or returns an escaping path | Pass skipped (throw) or entry skipped (escape) with a log entry — never a crash. |
| `OnVolumeExceeded` handler throws | Caught and logged; the scan completes. |
| `OnFreeSpaceReached` handler throws | Caught and logged; the check completes. |
| Free-space probe fails (volume gone, permission denied) | Logged by the loop; retried at the next interval — the host stays up. |

---

## FilePathResolver

Companion component of this package, namespace `Sa.Data.TempFolder.FilePathResolver`.

Resolves fuzzy / imprecise paths (as stored in a database) into real paths on the file system.
Every discovered mapping is cached per database path, so subsequent lookups are a dictionary
read plus a single file probe.

Public surface: `IFilePathResolver` (the DI service type), `FilePathResolver` (the default
implementation, fluent configuration included) and `IFileSystemService` (the disk seam; the
disk-backed `DefaultFileSystemService` stays `internal`).

Resolution cascade (first hit wins):

1. **Direct** — the database path is an absolute path that literally exists.
2. **Relative** — the path exists relative to `ConfiguredPath`.
3. **Incremental** — trailing components of the database directory are matched against
   directories under `ConfiguredPath`, innermost first — so a lost leading prefix
   (`/storage/usbdisk1/mikopbx/astspool/monitor/2021/06/16/15/file.mp3` →
   `<root>/2021/06/16/15/file.mp3`) still resolves.
4. **Deep search** (opt-in) — recursive search by file name. Hits are *not* cached:
   a file found anywhere in the tree carries no directory mapping.

| Method | Description |
|--------|-------------|
| `ResolvePath(string, bool asRelativePath = false)` | Resolves the database path, or returns `null` when the file cannot be located. Throws `ArgumentException` on null/whitespace. |
| `WithPossibleExtensions(params string[])` | Extensions tried during fuzzy search when the exact name misses (`"mp3"` and `".mp3"` are equivalent). |
| `WithDeepSearch(bool)` | Enables / disables recursive search by file name. |
| `WithForceSearch(bool)` | When enabled, every call re-runs the full cascade instead of reading the per-file cache. |
| `WithMap(string)` | Explicit global mapping applied before the cascade: database paths starting with `map` are looked up directly under `ConfiguredPath`. |
| `IsResolved` / `IsDeepSearch` / `IsForceSearchEnabled` | Current resolver state. |
| `Resolved` (event) | Raised once, when the first mapping is established. |

```csharp
var resolver = FilePathResolver.Create("/records")
    .WithPossibleExtensions("mp3", "wav")
    .WithDeepSearch(true);

// "/storage/usbdisk1/mikopbx/astspool/monitor/2021/06/16/15/has-root.mp3"
//   → /records/2021/06/16/15/has-root.mp3  (directory-suffix match)
string? full = resolver.ResolvePath(dbPath);
string? rel  = resolver.ResolvePath(dbPath, asRelativePath: true); // "2021/06/16/15/has-root.mp3"
```

### Registration (keyed, as `AddSaTempFolder`)

| Call | Description |
|------|-------------|
| `services.AddFilePathResolver(name, configuredPath, configure?)` | Registers `IFilePathResolver` under `name` — one host can serve several named roots. |
| `services.AddFilePathResolver(configuredPath, configure?)` | The same under the default key `Setup.DefaultName` (`"default"`, the resolver's `Setup` class in `Sa.Data.TempFolder.FilePathResolver`). |
| `sp.GetRequiredKeyedService<IFilePathResolver>(name)` | Resolves the instance — a bare `GetRequiredService<IFilePathResolver>()` does not resolve keyed services. |

- Registration names must be unique: a duplicate throws `InvalidOperationException` at
  registration; a null/whitespace `name` or `configuredPath` throws `ArgumentException`.
- Registration is lazy: a root that does not exist surfaces as `DirectoryNotFoundException`
  at first resolve — never at registration.
- `configure` runs once, when the instance is built, and is the same fluent chain as direct
  construction: `r => r.WithMap(...).WithPossibleExtensions(...).WithDeepSearch(true)`.
- The disk seam resolves a keyed `IFileSystemService` first (per registration), then a plain
  one, and falls back to the disk-backed default.

```csharp
builder.Services.AddFilePathResolver("records", "/data/records",
    r => r.WithPossibleExtensions("mp3", "wav").WithDeepSearch(true));

// somewhere later:
var resolver = sp.GetRequiredKeyedService<IFilePathResolver>("records");
string? full = resolver.ResolvePath(dbPath);
string? rel  = resolver.ResolvePath(dbPath, asRelativePath: true);
```
