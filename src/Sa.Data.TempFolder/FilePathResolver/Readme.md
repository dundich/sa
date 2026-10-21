# FilePathResolver — Sa.Data.TempFolder

Companion component of the package: the namespace `Sa.Data.TempFolder.FilePathResolver` lives in
its own folder, made of two files.

| File | Contents |
|---|---|
| `FilePathResolver.cs` | `IFilePathResolver` (the contract), `IFileSystemService` (the file-system seam), `DefaultFileSystemService` (the internal disk-backed seam) and the `FilePathResolver` class — the resolver itself. |
| `Setup.cs` | `Setup.AddFilePathResolver(...)` — the keyed DI registration. |

## Purpose

The resolver turns the fuzzy / imprecise paths that legacy databases store
(`/storage/usbdisk1/mikopbx/astspool/monitor/2021/06/16/15/file.mp3`) into real paths on the file
system (`/records/2021/06/16/15/file.mp3`).

The problem it solves: the path in the row was recorded on a machine whose layout no longer exists
— the disk was mounted elsewhere, the root prefix changed, the file was moved under a new root, or
the extension differs from what the row says. A plain `File.Exists(dbPath)` answers "no" for all of
those; the resolver instead *searches* for the file under a configured root and remembers how it
was found.

The component does not depend on the temp folder itself (no `ITempFolder`, no cleanup, no naming
strategy) — only on the file system, and even that through a seam (`IFileSystemService`) that can
be swapped for a fake.

## Components

| Type | Visibility | Role |
|---|---|---|
| `IFilePathResolver` | public | The DI service type — a single method, `ResolvePath(string dbPath, bool asRelativePath = false)`. |
| `FilePathResolver` | public | The implementation: the resolution cascade, the per-file cache and the fluent configuration (`WithMap`, `WithPossibleExtensions`, `WithDeepSearch`, `WithForceSearch`). |
| `IFileSystemService` | public | The file-system seam — `FileExists`, `DirectoryExists`, `EnumerateFiles`. All disk access of the resolver goes through it, so the resolver is unit-testable without touching the disk. |
| `DefaultFileSystemService` | internal | The disk-backed `IFileSystemService` (`File` / `Directory`). A fake is registered instead when the seam is swapped. |

## The contract — `IFilePathResolver`

```csharp
string? ResolvePath(string dbPath, bool asRelativePath = false);
```

- `dbPath` — the path as stored in the database.
- `asRelativePath` — when `true`, the result is returned relative to `ConfiguredPath` whenever the
  resolved path lies under that root.
- Returns the resolved system path, or `null` when the file cannot be located. A missing file is
  never an exception.

## The resolver — `FilePathResolver`

`ResolvePath` runs a **cascade** — the first step that finds the file wins:

| # | Step | What it does |
|---|------|--------------|
| 1 | **Direct** | The database path is an absolute path that literally exists → it is used as is. |
| 2 | **Relative** | The path is not rooted and exists under `ConfiguredPath` → `<root>/<dbPath>`. |
| 3 | **Incremental** | The trailing components of the database directory are matched against directories under `ConfiguredPath`, innermost first: `/db/A/2024/a.mp3` → `<root>/A/2024/a.mp3`. A lost leading prefix still resolves. |
| 4 | **Deep search** (opt-in) | A recursive search by file name under `ConfiguredPath`. The hit carries no directory mapping, so it is **not cached** and every call re-enumerates. |

Inside one call the order is: the cached per-file mapping (unless force search is on) → the
explicit `WithMap` mapping → the cascade.

An explicit global mapping (`WithMap`) is tried before the cascade: a database path that starts with
the mapped prefix is looked up directly under `ConfiguredPath` (the prefix is stripped, the
remainder is the relative path). The prefix match is on a component boundary — `/archive/usbdisk1`
does not match `/archive/usbdisk10`.

```csharp
var resolver = FilePathResolver.Create("/records")
    .WithPossibleExtensions("mp3", "wav")
    .WithDeepSearch(true);

// "/storage/usbdisk1/mikopbx/astspool/monitor/2021/06/16/15/has-root.mp3"
//   → /records/2021/06/16/15/has-root.mp3  (directory-suffix match)
string? full = resolver.ResolvePath(dbPath);
string? rel  = resolver.ResolvePath(dbPath, asRelativePath: true); // "2021/06/16/15/has-root.mp3"
```

### Caching

Every discovered mapping is remembered per database path (`ConcurrentDictionary`), so the second
lookup for the same path is a dictionary read plus one file probe instead of a full cascade.

- **Hits are cached, misses are not** — a file that appears later is found by the next lookup.
- **Deep-search hits are not cached** — a file found anywhere in the tree carries no reliable
  directory mapping.
- A cached mapping that stops resolving (the file was moved) is re-searched: the cascade runs
  again and, with deep search on, the file is found at its new place.
- `WithForceSearch(true)` re-runs the whole cascade on every call and rewrites the cache — useful
  when the layout changes under a running process.

### Extensions

`WithPossibleExtensions("mp3", "wav")` lists the extensions to try when the exact file name misses:
the resolver probes `file.mp3` first and then `file.wav` (an extension without the dot is
equivalent). The list is normalised (a leading dot is added, duplicates are dropped) and exposed as
`PossibleExtensions`. It also narrows the deep search: with extensions configured, only files with
one of those extensions match.

### Relative results

`ResolvePath(dbPath, asRelativePath: true)` returns the path relative to `ConfiguredPath` when the
resolved file lies under that root (`2021/06/16/15/file.mp3`), and the absolute path unchanged when
it lies outside it (a directly-resolved literal path).

### The `Resolved` event

`Resolved` fires **once** — when the first mapping is established. An explicit `WithMap` counts as
a mapping and raises it right away; a deep-search hit does not (there is no mapping to report).
`IsResolved` reports the same state without the event.

### Public surface

| Member | Description |
|--------|-------------|
| `FilePathResolver.Create(string configuredPath)` | Creates a resolver over the disk-backed default file system. |
| `FilePathResolver.ConfiguredPath` | The root every lookup is relative to (normalised, no trailing separator). |
| `FilePathResolver.WithMap(string map)` | Explicit global mapping, applied before the cascade. |
| `FilePathResolver.WithPossibleExtensions(params string[])` | Extensions tried during the fuzzy search. |
| `FilePathResolver.WithDeepSearch(bool)` | Enables / disables the recursive search by file name. |
| `FilePathResolver.WithForceSearch(bool)` | Re-runs the cascade on every call instead of reading the cache. |
| `FilePathResolver.PossibleExtensions` | The normalised extension list (empty when none are configured). |
| `FilePathResolver.IsResolved` / `IsDeepSearch` / `IsForceSearchEnabled` | The current resolver state. |
| `FilePathResolver.Resolved` (event) | Raised once, when the first mapping is established. |
| `FilePathResolver.ResolvePath(string dbPath, bool asRelativePath = false)` | Resolves the database path, or returns `null` when the file cannot be located. Throws `ArgumentException` on a null/whitespace path. |

## The file-system seam — `IFileSystemService`

```csharp
bool FileExists(string path);
bool DirectoryExists(string path);
IEnumerable<string> EnumerateFiles(string directory, string pattern);
```

All disk access of `FilePathResolver` goes through this interface, so the resolver is unit-testable
without touching the disk. `DefaultFileSystemService` is the disk-backed default (`File.Exists`,
`Directory.Exists`, `Directory.EnumerateFiles(..., SearchOption.AllDirectories)`) and stays
`internal`.

## DI registration — `Setup.cs`

```csharp
builder.Services.AddFilePathResolver("records", "/data/records",
    r => r.WithPossibleExtensions("mp3", "wav").WithDeepSearch(true));

// somewhere later:
var resolver = sp.GetRequiredKeyedService<IFilePathResolver>("records");
string? full = resolver.ResolvePath(dbPath);
string? rel  = resolver.ResolvePath(dbPath, asRelativePath: true);
```

| Call | Description |
|------|-------------|
| `services.AddFilePathResolver(name, configuredPath, configure?)` | Registers `IFilePathResolver` under `name` — one host can serve several named roots with different maps, extensions and search options. |
| `services.AddFilePathResolver(configuredPath, configure?)` | The same under the default key `Setup.DefaultName` (`"default"`). |
| `sp.GetRequiredKeyedService<IFilePathResolver>(name)` | Resolves the instance (a singleton per key). A bare `GetRequiredService<IFilePathResolver>()` does **not** resolve keyed services. |

- **Names must be unique**: a duplicate throws `InvalidOperationException` at registration; a
  null/whitespace `name` or `configuredPath` throws `ArgumentException`.
- **Registration is lazy**: the instance is built on first resolve, so a root that does not exist
  surfaces as `DirectoryNotFoundException` there — never at registration.
- `configure` runs once, when the instance is built, and is the same fluent chain as direct
  construction: `r => r.WithMap(...).WithPossibleExtensions(...).WithDeepSearch(true)`.
- **The file-system seam** resolves a keyed `IFileSystemService` first (per registration, keyed by
  the registration name), then a plain one, and falls back to the disk-backed default:

```csharp
services.AddKeyedSingleton<IFileSystemService>("records", new FakeFileSystem());
```

## Errors

| Situation | Result |
|-----------|--------|
| The database path is null / whitespace | `ArgumentException` |
| The configured root does not exist | `DirectoryNotFoundException` at construction — and therefore at first resolve when registered through DI |
| The file cannot be located | `null` — not an exception |
| A registration name is already taken | `InvalidOperationException` at registration |
| `name` / `configuredPath` is null or whitespace | `ArgumentException` at registration |

## Notes

- The resolver is thread-safe: the per-file cache is a `ConcurrentDictionary`, and the `Resolved`
  event fires exactly once even under concurrent lookups.
- The resolver never writes to the file system — it only probes it.
- Paths are normalised to the platform separator, and a trailing separator is dropped.
