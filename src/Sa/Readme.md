# Sa — Shared Utilities

Core utility library for the **Sa** ecosystem. Contains shared classes and extension methods consumed by other packages via `<Compile Include="..." Link="..."/>`. Targets **.NET 10.0**, **Native AOT compatible**.

---

## Classes (namespace `Sa.Classes`)

### LockRenewer

Automatic lock extension with configurable renewal interval using `PeriodicTimer`.

| Method | Description |
|--------|-------------|
| `KeepLocked(TimeSpan, Func<CancellationToken, Task>, bool, CancellationToken)` | Runs a background task that periodically extends a lock. Returns `IAsyncDisposable` for clean shutdown. |
| `WaitForConditionAsync(Func<CancellationToken, Task<bool>>, TimeSpan, TimeSpan?, CancellationToken)` | Polls a predicate until it returns `true`, timeout expires, or cancellation is requested. |

```csharp
var disposable = LockRenewer.KeepLocked(
    lockExpiration: TimeSpan.FromSeconds(30),
    extendLocked: ct => db.ExtendLockAsync(resourceId, ct),
    blockImmediately: true);

// ... later
await disposable.DisposeAsync();
```

### MurmurHash3

Compact, fast hash function for type identification and partitioning.

| Method | Description |
|--------|-------------|
| `Hash32(ReadOnlySpan<byte>, uint)` | Computes a 32-bit MurmurHash3 hash with seed. StackAlloc-friendly. |

```csharp
var bytes = Encoding.UTF8.GetBytes("partition-key");
uint hash = MurmurHash3.Hash32(bytes, seed: 0);
int partitionIndex = (int)(hash % numberOfPartitions);
```

### Retry

Retry helpers with four strategies: **Constant**, **Linear**, **Exponential**, and **Jitter** (Azure-style decorrelated jitter).

#### Strategies

| Strategy | Parameters | Behavior |
|----------|------------|----------|
| `Constant` | `delay`, `fastFirst` | Fixed delay between retries. `fastFirst: true` skips initial delay. |
| `Linear` | `firstDelay`, `increment`, `maxDelay`, `count` | Delay increases linearly: `firstDelay + n * increment`, capped at `maxDelay`. |
| `Exponential` | `firstDelay`, `factor`, `maxDelay`, `count` | Delay doubles each attempt: `firstDelay * factor^n`, capped at `maxDelay`. |
| `Jitter` | `strategy`, `jitterSpan` | Applies random +/- jitter to any strategy's delay. |

#### Execution

| Method | Description |
|--------|-------------|
| `WaitAndRetry(IEnumerable<TimeSpan>, Func<Task>, CancellationToken)` | Executes a function with retry delays between attempts. Throws the last exception if cancelled during a delay after a failure. |

```csharp
// Exponential backoff: 100ms → 200ms → 400ms → 800ms → 1600ms (max)
var strategy = Retry.Exponential(firstDelay: TimeSpan.FromMilliseconds(100), count: 5);
await Retry.WaitAndRetry(strategy, () => CallExternalApiAsync(), cancellationToken);
```

### ResetLazy\<T\>

Lazily-evaluated, resettable cached value with three thread-safety modes.

| Property/Method | Description |
|-----------------|-------------|
| `Value` | Lazily initializes and returns the cached value. |
| `IsValueCreated` | `true` if the factory has been invoked. |
| `Load()` | Force initialization (no-op if already created). |
| `Reset()` | Clears the cache, optionally invoking a `valueReset` callback on the old value. |

```csharp
var lazy = new ResetLazy<MyConfig>(() => ConfigLoader.Load(), valueReset: cfg => cfg.Dispose());
var config = lazy.Value;        // first access triggers factory
lazy.Reset();                   // clears cache, calls Dispose on old config
config = lazy.Value;            // factory invoked again
```

### Levenshtein

Damerau-Levenshtein distance algorithm for string similarity comparison. Optimized with stackalloc for minimal allocations.

| Method | Description |
|--------|-------------|
| `Distance(string?, string?)` | Returns edit distance (0 = exact match). Null-safe. |
| `GetSimilarity(string?, string?)` | Returns similarity ratio 0.0–1.0 based on longest string. |
| `IsSimilar(string?, string?, double threshold)` | Checks if similarity ≥ threshold (default 0.8). |

#### Levenshtein.Matcher

Generic fuzzy matching over collections.

| Method | Description |
|--------|-------------|
| `FindMatches<T>(string?, IEnumerable<T>, Func<T, string?>, double, bool)` | Yields all matches above `similarityThreshold`. Normalizes by default. |
| `FindBestMatch<T>(...)` | Returns the single best match (highest similarity). |
| `FindBestMatch(IEnumerable<MatchResult<T>>)` | Selects best from pre-filtered matches. |
| `FindBestMatch(string?, params string?[])` | Overload for plain-string candidate arrays. Returns `(bestMatch, distance)`. |

```csharp
var best = Levenshtein.Matcher.FindBestMatch(
    source: "recieve",
    candidates: ["receive", "relief", "refuse"]);
// best.bestMatch == "receive", best.distance == 1

bool similar = Levenshtein.IsSimilar("hello", "hallo", threshold: 0.8);
// true
```

### MimeTypeMap

Comprehensive MIME type lookup by file extension or filename (1000+ mappings sourced from Windows Registry + IANA).

| Method | Description |
|--------|-------------|
| `TryGetMimeType(string, out string?)` | Looks up MIME type from filename or extension. Strips query strings automatically. |
| `GetMimeType(string)` | Same as `TryGetMimeType` but returns `"application/octet-stream"` on miss. |
| `GetExtension(string mimeType, bool throwErrorIfNotFound)` | Reverse lookup: MIME type → extension. |

```csharp
string? mime;
if (MimeTypeMap.TryGetMimeType("document.pdf", out mime))
{
    Console.WriteLine(mime); // "application/pdf"
}

string ext = MimeTypeMap.GetExtension("image/png"); // ".png"
```

### ProcessExecutor / IProcessExecutor

Asynchronous process executor with real-time output handling, stdin piping, and robust lifecycle management.

| Method | Description |
|--------|-------------|
| `ExecuteAsync(ProcessStartInfo, Action<string>?, Action<string>?, TimeSpan?, CancellationToken)` | Real-time stdout/stderr callbacks. |
| `ExecuteWithResultAsync(...)` | Captures full output into `ProcessExecutionResult`. |
| `ExecuteStdOutAsync(...)` | Streams stdout to a callback while piping stdin and collecting stderr. |

#### Public Types

| Type | Description |
|------|-------------|
| `ProcessExecutionResult` | Record: `(int ExitCode, string StandardOutput, string StandardError)` |
| `ProcessExecutionException` | Thrown on non-zero exit code with `Exitcode` property. |
| `ProcessExecutionResultException` | Wraps `ProcessExecutionResult` as an exception. |
| `ProcessStartException` | Thrown when `Process.Start()` fails. |
| `ProcessTimeoutException` | Thrown on execution timeout. |

```csharp
var result = await IProcessExecutor.Default.ExecuteWithResultAsync(new ProcessStartInfo
{
    FileName = "ffmpeg",
    Arguments = "-i input.mp4 -vn -ab 128k output.mp3",
    RedirectStandardOutput = true,
    RedirectStandardError = true
});

if (result.ExitCode != 0)
    Console.WriteLine(result.StandardError);
```

---

## Extensions (namespace `Sa.Extensions`)

### DateTimeExtensions

| Method | Description |
|--------|-------------|
| `ToUnixTimestamp(bool isInMilliseconds)` | Converts `DateTime` to Unix epoch seconds or milliseconds. Auto-converts non-UTC to UTC. |
| `StartOfDay()` | Returns `DateTimeOffset` at midnight with same offset. |
| `EndOfDay()` | Returns `DateTimeOffset` at the start of the next day (exclusive upper bound). |
| `StartOfMonth()` / `EndOfMonth()` | First/next-day-of-month boundaries. |
| `StartOfYear()` / `EndOfYear()` | First/next-year boundaries. |

```csharp
var ts = DateTime.UtcNow.ToUnixTimestamp();           // seconds
var ms = someDate.ToUnixTimestamp(isInMilliseconds);  // milliseconds
var today = dto.StartOfDay();
```

### NumericExtensions

| Method | Description |
|--------|-------------|
| `ToDateTimeFromUnixTimestamp(this uint)` | Unix timestamp → UTC `DateTime` (auto-detects seconds vs milliseconds). |
| `ToDateTimeFromUnixTimestamp(this long)` | Same for signed 64-bit. |
| `ToDateTimeFromUnixTimestamp(this ulong)` | Unsigned 64-bit. |
| `ToDateTimeFromUnixTimestamp(this double)` | Floating-point seconds, truncated to long. |
| `ToDateTimeFromUnixTimestamp(this string)` | Parses string → long → DateTime; returns `null` on parse failure. |
| Nullable overloads (`long?`, `ulong?`, `double?`) | Return `null` when input is `null`. |
| `ToDateTimeOffsetFromUnixTimestamp(this long)` | Timestamp → `DateTimeOffset`. |

```csharp
DateTime dt = 1700000000L.ToDateTimeFromUnixTimestamp();
DateTime? maybe = "1700000000".ToDateTimeFromUnixTimestamp();  // not null
```

### EnumerableExtensions

| Method | Description |
|--------|-------------|
| `JoinByString<T>(IEnumerable<T>, string?)` | Joins elements using `string.Join`. Null-safe. |
| `JoinByString<T>(IEnumerable<T>, Func<T,T>, string?)` | Maps then joins. Fast path uses `ICollection<T>.Count` for pre-allocation. |
| `JoinByString<T>(IEnumerable<T>, Func<T,int,T>, string?)` | Map-with-index then join. |

```csharp
var csv = new[] { 1, 2, 3 }.JoinByString(", ");     // "1, 2, 3"
var joined = items.JoinByString(x => x.Name, "|");   // "Name1|Name2|..."
```

### ExceptionExtensions

| Method | Description |
|--------|-------------|
| `IsCritical(this Exception)` | Returns `true` for fatal CLR exceptions: `OutOfMemoryException`, `StackOverflowException`, `AppDomainUnloadedException`, `BadImageFormatException`, `CannotUnloadAppDomainException`, `InvalidProgramException`, `ThreadAbortException`. |
| `GetErrorMessages(this Exception)` | Concatenates all exception messages from root cause to outermost, one per line. |

```csharp
if (ex.IsCritical()) Environment.FailFast(ex.Message);
Console.WriteLine(ex.GetErrorMessages());
```

### SpanExtensions

| Method | Description |
|--------|-------------|
| `GetChunks<T>(Memory<T>, int)` | Yields `Memory<T>` chunks via iterator. |
| `GetChunksArray<T>(Memory<T>, int)` | Materialized `Memory<T>[]` with pre-allocated capacity. |
| `SelectWhere<T,TResult>(Span<T>, Func<T,int,TResult>, Func<TResult,int,bool>?)` | Combined Select+Where with index on `Span<T>`. Returns trimmed array. |
| `SelectWhere<T,TResult>(Span<T>, Func<T,TResult>, Func<TResult,bool>?)` | Same without index. |
| `SelectWhere<T,TResult>(ReadOnlySpan<T>, ...)` | Overloads for `ReadOnlySpan<T>`. |

```csharp
var chunks = someMemory.GetChunksArray(256);       // Memory<byte>[]
var filtered = span.SelectWhere(x => x * 2, v => v > 10);
```

### StringExtensions

| Method | Description |
|--------|-------------|
| `NullIfEmpty(this string?)` | Returns `null` if the string is null, empty, or whitespace-only. Otherwise returns the original. |
| `NormalizeWhiteSpace(bool isTrimmed)` | Collapses consecutive whitespace/separators/control chars into a single space. Zero-allocation fast path for clean strings. |
| `NormalizeWhiteSpaceSpan(ReadOnlySpan<char>, Span<char>, bool)` | Span-based zero-allocation variant. Writes into destination buffer, returns written length. |
| `GetMurmurHash3(uint seed)` | Computes MurmurHash3 of UTF-8 encoding without allocating a byte array. StackAlloc up to 512 bytes. |

```csharp
string? cleaned = "  hello   world  ".NormalizeWhiteSpace();  // "hello world"
uint hash = "key".GetMurmurHash3(seed: 42);
string? blank = "   ".NullIfEmpty();  // null
```

### StrToExtensions

Safe parsing extensions returning nullable result (`T?`) — never throw on bad input.

| Method | Input Type | Returns |
|--------|-----------|---------|
| `StrToBool(string? / ReadOnlySpan<char>)` | string / span | `bool?` |
| `StrToInt(string? / ReadOnlySpan<char>)` | string / span | `int?` |
| `StrToShort(string? / ReadOnlySpan<char>)` | string / span | `short?` |
| `StrToUShort(string? / ReadOnlySpan<char>)` | string / span | `ushort?` |
| `StrToLong(string? / ReadOnlySpan<char>)` | string / span | `long?` |
| `StrToULong(string? / ReadOnlySpan<char>)` | string / span | `ulong?` |
| `StrToDouble(string? / ReadOnlySpan<char>)` | string / span | `double?` |
| `StrToGuid(string? / ReadOnlySpan<char>)` | string / span | `Guid?` |
| `StrToBytes(string, Encoding?)` | string | `byte[]` (UTF-8 by default) |
| `StrToEnum<T>(string?, T defaultValue)` | string? | `T` (возвращает `defaultValue` при неудаче, case-insensitive) |
| `StrToDate(string? / ReadOnlySpan<char>, IFormatProvider?, DateTimeStyles)` | string / span | `DateTime?` — tries ~60 date formats including ISO 8601 round-trip |

```csharp
int? port = "8080".StrToInt();              // 8080
Guid? id = "not-a-guid".StrToGuid();        // null
DateTime? dt = "2024-01-15".StrToDate();    // parsed or null
string? mode = "red".StrToEnum("black");  // "red" (case-insensitive)
```

### JsonExtensions

| Method | Description |
|--------|-------------|
| `ToJson<T>(T, JsonSerializerOptions?)` | Serializes to JSON string. |
| `FromJson<T>(string, JsonSerializerOptions?)` | Deserializes from JSON string. |

Both methods carry `[RequiresUnreferencedCode]` and `[RequiresDynamicCode]` attributes for AOT compatibility warnings. See `JsonHttpResultTrimmerWarning.SerializationUnreferencedCodeMessage` / `SerializationRequiresDynamicCodeMessage` for guidance.

---

## Range & Section Types (namespace `Sa.Classes`)

Interval types for defining retry delays, batch sizes, and other bounded ranges.

### LimSection\<T\>

Closed interval `[min, max]`.

| Member | Description |
|--------|-------------|
| Constructor `(T min, T max)` | Creates a closed interval. Throws if `min > max`. |
| `Min` / `Max` | Interval boundaries. |

### HalfSection\<T\>

Half-open intervals: `OpenMin(min)` = `(min, ∞)` or `OpenMax(max)` = `(-∞, max]`.

| Member | Description |
|--------|-------------|
| `Kind` | `OpenMin` or `OpenMax`. |
| `Bound` | The finite boundary value. |

### Section\<T\>

Union type wrapping `LimSection<T>` or `HalfSection<T>`. Unified extension methods:

| Extension | Description |
|-----------|-------------|
| `Contains(Range<T>, T)` | Checks if value falls within the section. |
| `Expand<T>(Section<T>, T, T)` | Widens the section to include new bounds. |
| `Shrink<T>(Section<T>, T, T)` | Narrows the section. |
| `Center<T>(Section<T>)` | Midpoint of a `LimSection`. |
| `Width<T>(LimSection<T>)` | Distance between min and max. |
| `ApplyToBounds<T>(Section<T>, T, T)` | Clamps arbitrary bounds to the section. |
| `WithinBounds<T>(Section<T>, T, T)` | Checks if two values fit inside the section. |
| `Overlaps<T>(Section<T>, Section<T>)` | Tests intersection between sections. |
| `MergeSections<T>(Section<T>, Section<T>)` | Creates a section covering both inputs. |
| `GenerateValues<T>(Section<T>, int, Func<T, T, IEnumerable<T>>)` | Generates N values spanning the section (for LINQ `Range`). |

```csharp
// Retry delays from 100ms to 5 seconds
Section<TimeSpan> delays = new LimSection<TimeSpan>(
    TimeSpan.FromMilliseconds(100),
    TimeSpan.FromSeconds(5));

bool inRange = delays.Contains(TimeSpan.FromSeconds(1)); // true
```

---

## Architecture Notes

- All types are **internal** except where explicitly marked public (`ProcessExecutionResult`, `ProcessExecutionException`, etc.)
- Shared via `<Compile Include="..." Link="..." />` pattern — linked into downstream projects, not referenced as NuGet packages
- Fully **Native AOT compatible** — no reflection-based serialization, no dynamic IL generation
- Zero-dependency: no external NuGet packages
