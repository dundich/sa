# Sa.Media

**English** | [Русский](Readme-ru.md)

Async, memory-efficient WAV reader for .NET 10+. Designed for Native AOT compatibility with zero allocations on hot paths. Includes a stereo cross-feed suppression pipeline (`Sa.Media.Echo`) for separating closely miked sources.

## Installation

```bash
dotnet add package Sa.Media
```

---

## Contents

- [Installation](#installation)
- [Features](#features)
- [Quick Start](#quick-start)
  - [Read header](#read-header)
  - [Read samples per channel in batches](#read-samples-per-channel-in-batches)
  - [Read in another encoding](#read-in-another-encoding)
  - [Buffer ownership](#buffer-ownership)
  - [Byte offsets for a time range](#byte-offsets-for-a-time-range)
- [Cross-feed suppression (`Sa.Media.Echo`)](#cross-feed-suppression-samediaecho)
- [Supported Formats](#supported-formats)
- [Public API Reference](#public-api-reference)
  - [Core types](#core-types)
  - [Echo types (`Sa.Media.Echo`)](#echo-types-samediaecho)
  - [Key methods on `AsyncWavReader`](#key-methods-on-asyncwavreader)
  - [`WavHeader`](#wavheader)
  - [`TimeRange` helpers](#timerange-helpers)
- [Performance Notes](#performance-notes)
- [Project Layout](#project-layout)
- [License](#license)

## Features

- **Fully asynchronous** — `PipeReader`-based streaming, no blocking I/O
- **Memory efficient** — `ArrayPool` buffer reuse, one rented buffer per enumeration
- **Multi-format input** — PCM 8/16/24/32-bit, IEEE Float 32/64-bit, `WAVE_FORMAT_EXTENSIBLE`
- **Any target encoding** — `AudioEncoding.Pcm8/16/24/32`, `IeeeFloat32/64`; PCM16→PCM16 is copied straight through, everything else goes via normalized `[-1.0, 1.0]`
- **Chunk-aware** — `JUNK`, `LIST`, `bext` and any other chunk before `data` is skipped, including zero-size ones
- **Stream-safe** — a partial frame at a buffer boundary can never spin `PipeReader`; the tail is dropped
- **Streaming recordings** — `data` size `0xFFFFFFFF` is resolved from the file length when the stream is seekable
- **Cross-feed suppression** — stereo separation pipeline in `Sa.Media.Echo`

---

## Quick Start

### Read header

```csharp
await using var reader = AsyncWavReader.CreateFromFile("test.wav");

var header = await reader.GetHeaderAsync(ct);
Console.WriteLine($"{header.NumChannels}ch @ {header.SampleRate}Hz, " +
    $"{header.BitsPerSample}-bit {header.AudioFormat}, {header.GetDuration()}");
```

`Create(Stream)` leaves the caller's stream open; `CreateFromFile` owns the `FileStream` and closes it.

### Read samples per channel in batches

```csharp
await using var reader = AsyncWavReader.CreateFromFile("test.wav");

await foreach (AudioChunk chunk in reader.ReadChannelChunksAsync(
    channel: 0,          // 0-based
    samplesPerBatch: 4096,
    cancellationToken: ct))
{
    // chunk.Samples  — samples of one channel, `samplesPerBatch * bytesPerSample` bytes
    // chunk.Position — byte offset of the first sample from the start of the `data` chunk
    // chunk.IsEof    — true for the last chunk (may be shorter than samplesPerBatch)
}
```

Each chunk is produced from whole frames, so `Position` is always frame-aligned and the
enumerator ends where the `data` chunk ends — trailing chunks are never read as audio.

The channel enumerator reads the pipe strictly sequentially: enumerating channel 0 and
channel 1 at the same time on one reader is not supported. Open the file twice (as the
Echo pipeline does) if you need both channels.

### Read in another encoding

```csharp
await foreach (AudioChunk chunk in reader.ReadChannelChunksAsync(
    channel: 1,
    samplesPerBatch: 1024,
    format: AudioEncoding.Pcm24BitSigned,   // default: Pcm16BitSigned
    cancellationToken: ct))
{
    // Raw 24-bit little-endian PCM, 3 bytes per sample.
}
```

Source `PCM16` → target `PCM16` is a straight copy. Every other pair is converted through
the normalized value `[-1.0, 1.0]`; integer targets clamp (NaN becomes silence), IEEE-float
targets keep values above ±1.0 as-is.

### Buffer ownership

`AudioChunk.Samples` points at a **pooled** buffer owned by the enumeration:

- it is valid until the next `MoveNextAsync()` (and until the enumeration finishes);
- copy it if it must outlive the current iteration;
- breaking out of `await foreach` returns the buffer to the pool.

### Byte offsets for a time range

The reader always streams the whole `data` chunk from its beginning; it does not seek.
`WavHeader.CalculateCutOffsets` gives the byte offsets of a `TimeRange` inside the file,
so a caller that does its own I/O can cut precisely on frame boundaries:

```csharp
WavHeader header = await reader.GetHeaderAsync(ct);

// [start, end) byte offsets of seconds 5–15, aligned to whole frames and clamped
// to the audio data (works for streamed recordings when fileSize is supplied).
(long from, long to) = header.CalculateCutOffsets(TimeRange.Seconds(5, 15), fileSize);
```

---

## Cross-feed suppression (`Sa.Media.Echo`)

Separates a stereo recording of two closely miked speakers/microphones into two
near-separate tracks. The algorithm estimates the acoustic cross-talk between channels
(`y_L = x_L + β·x_R`, `y_R = x_R + α·x_L`) and inverts the model.

Supports two strategies:

- **Linear** — cross-talk cancellation only (fast, low memory). Three implementations:
  in-memory, streaming with an exact peak pass, streaming with a fast peak bound.
- **Aggressive** — cross-talk cancellation plus per-frequency spectral masking for sharper
  separation (higher CPU and memory: the whole recording is loaded, up to ~100 M frames).

```csharp
using Sa.Media.Echo;

// Automatic mode for the linear path: in-memory for short clips, streaming for long ones.
await CrossFeedSeparator.ExecuteAsync(new AudioSeparationOptions(
    InputPath: "recording.wav",
    OutputPath: "separated.wav"), CancellationToken.None);

// Aggressive mode with spectral masking
await CrossFeedSeparator.ExecuteAsync(new AudioSeparationOptions(
    InputPath: "recording.wav",
    OutputPath: "separated_aggressive.wav",
    Processing: CrossFeedSeparator.ProcessingMode.Aggressive,
    DominanceThresholdDb: 6.0,   // dB threshold for single-speaker regions
    SpectralMaskPower: 4.0,      // spectral mask sharpening exponent
    MaskFloor: 0.01),            // minimum mask floor [0, 1]
    CancellationToken.None);
```

- Input must be a **2-channel (stereo)** WAV; anything else throws `InvalidOperationException`.
- Output is 16-bit PCM WAV, peak-normalized to 0.98.
- When `OutputPath` is omitted the result is written next to the input as
  `<name>_linear.wav` / `<name>_aggressive.wav`.
- `OutputPath` **must not point at the input file**: the streaming modes create the output
  before the input is fully read, so `ArgumentException` is thrown instead of truncating
  the recording.
- The linear path picks the mode from the input size: up to 32 M frames (~12 minutes at
  44.1 kHz) are processed in memory.

---

## Supported Formats

| Format | Read | Write |
|--------|------|-------|
| PCM 8-bit (unsigned) | ✅ | ✅ |
| PCM 16-bit (signed) | ✅ | ✅ |
| PCM 24-bit (signed) | ✅ | ✅ |
| PCM 32-bit (signed) | ✅ | ✅ |
| IEEE Float 32-bit | ✅ | ✅ |
| IEEE Float 64-bit | ✅ | ✅ |

Mono and stereo (up to 6 channels) are accepted. `WAVE_FORMAT_EXTENSIBLE` is read when its
`SubFormat` is PCM or IEEE float. Unknown chunks (`JUNK`, `LIST`, `bext`, …) between `fmt `
and `data` are skipped — including zero-size ones — and a missing `fmt ` chunk is reported
as `InvalidDataException` instead of being misread as data.

The Echo pipeline reads any of the formats above and always writes 16-bit PCM.

---

## Public API Reference

### Core types

| Type | Description |
|------|-------------|
| `AsyncWavReader` | Main async WAV reader — create from `Stream` or file path |
| `WavHeader` | Parsed RIFF/WAV header with computed properties (`IsPcm`, `IsStereo`, `GetDuration()`, …) |
| `AudioChunk` | Record: `(ReadOnlyMemory<byte> Samples, long Position, bool IsEof)` — samples of one channel |
| `AudioEncoding` | Enum: PCM 8/16/24/32, IEEE Float 32/64 |
| `WaveFormatType` | Enum: `Pcm`, `Adpcm`, `IeeeFloat`, `Extensible` |
| `TimeRange` | Record: `(From, To)` with factories `Create`, `Ms`, `Seconds`, `RangeFromDuration`, `Default` |

### Echo types (`Sa.Media.Echo`)

| Type | Description |
|------|-------------|
| `CrossFeedSeparator` | Stereo cross-feed suppression pipeline (entry point) |
| `CrossFeedSeparator.ProcessingMode` | Enum: `Auto`, `MaximumSpeed`, `MinimumMemory`, `MinimumMemoryFastNormalize`, `Aggressive` |
| `AudioSeparationOptions` | Record: `InputPath`, `OutputPath`, `Processing`, `DominanceThresholdDb`, `SpectralMaskPower`, `MaskFloor` |

### Key methods on `AsyncWavReader`

| Method | Returns | Description |
|--------|---------|-------------|
| `Create(Stream, StreamPipeReaderOptions?)` | `AsyncWavReader` | Factory from a stream; the stream stays open (pass `leaveOpen: false` to transfer ownership) |
| `CreateFromFile(string, FileStreamOptions?)` | `AsyncWavReader` | Factory from a file path; the reader owns the `FileStream` |
| `GetHeaderAsync(ct)` | `Task<WavHeader>` | Thread-safe lazy header parsing; not poisoned by the first caller's cancellation |
| `ReadChannelChunksAsync(channel, samplesPerBatch, format, ct)` | `IAsyncEnumerable<AudioChunk>` | Batched samples for a single channel |
| `Dispose()` / `DisposeAsync()` | — | Idempotent; completes the `PipeReader` and disposes the owned file |

### `WavHeader`

| Member | Description |
|--------|-------------|
| `Validate()` | Throws `NotSupportedException`/`InvalidDataException` for formats or field combinations the reader cannot handle |
| `GetDurationInSeconds(long? fileSize)` / `GetDuration()` | Duration; for streamed recordings pass the file length, otherwise `0` |
| `GetBytesPerSample()` / `SampleSize` | Bytes per single-channel sample (`BitsPerSample / 8`, `BlockAlign / NumChannels`) |
| `BlockAlign` / `GetBytesPerSecond()` | Bytes per frame / bytes per second |
| `IsPcm`, `IsIeeeFloat`, `IsFloat`, `IsIntegerPcm`, `IsExtensible` | Sample format, with `WAVE_FORMAT_EXTENSIBLE` sub-format resolved |
| `IsMono`, `IsStereo`, `NumChannels`, `SampleRate`, `BitsPerSample` | Layout |
| `HasDataSize`, `DataSize`, `DataOffset` | `data` chunk; `HasDataSize == false` for streamed recordings |
| `CalculateCutOffsets(range, fileSize, alignToFrames)` | `(long cutFrom, long cutTo)` byte offsets of a `TimeRange` |
| `GetNormalizedConverter()` | `Func<ReadOnlySpan<byte>, double>` mapping a raw sample to `[-1.0, 1.0]` |
| `ToString()` | Human-readable multi-line summary |

### `TimeRange` helpers

| Method | Description |
|--------|-------------|
| `TimeRange.Create(from, to)` | From/to `TimeSpan` bounds |
| `TimeRange.Ms(from, to)` | Millisecond bounds; `long.MaxValue` means open-ended |
| `TimeRange.Seconds(from, to)` | Double-second bounds; `to` omitted means open-ended |
| `TimeRange.RangeFromDuration(from, duration)` | From start + duration |
| `TimeRange.Default` | `[0, ∞)` |
| `range.Contains(time)` | Inclusive containment (by `TimeSpan`, `long` or `double` ms) |
| `Span<TimeRange>.Sort()` | Sort in place by `From` |
| `ranges.Merge(thresholdMs)` | Merge ranges closer than the threshold |
| `TimeRangeExtensions.ExpandTimeRanges(chunks, thresholdMs, gapMs)` | Merge and stretch neighbouring ranges, preserving the chunk layout |

---

## Performance Notes

- Only one output buffer (and one scratch sample) is rented per enumeration, regardless of file size.
- `Position` tracking and frame parsing work on `ReadOnlySequence<byte>` — no per-sample allocations and no `double` round-trip for `PCM16 → PCM16`.
- All internal awaits use `ConfigureAwait(false)` — safe in any synchronization context.
- The reader never seeks: for trim-style access, compute offsets with `CalculateCutOffsets` and cut in your own I/O.

---

## Project Layout

```
src/Sa.Media/
├── AsyncWavReader.cs        # Main reader: header, per-channel chunk enumeration
├── AudioChunk.cs            # Pooled batch of one channel's samples
├── AudioEncoding.cs         # Target encoding enum
├── AudioEncodingExtensions.cs
├── BinaryPipeReader.cs      # Little-endian reader over PipeReader
├── PipeReaderExtensions.cs  # Skip helper
├── SampleConverter.cs       # PCM/float ↔ normalized double
├── TimeRange.cs             # Time interval
├── TimeRangeExtensions.cs   # Contains, Sort, Merge, ExpandTimeRanges
├── WavHeader.cs             # RIFF header model + offsets/duration
├── WavHeaderReader.cs       # Header parser (chunk skipping included)
├── WaveFormatType.cs        # Format tag enum
└── WaveFormatTypeExtensions.cs

src/Sa.Media/Echo/
├── CrossFeedSeparator.cs     # Entry point — estimate + invert cross-talk
├── AudioSeparationOptions.cs # Public options record
├── AggressiveMode.cs         # STFT-based spectral masking
├── AudioMath.cs              # Coefficient math, percentile, normalization
├── AudioConstants.cs         # Tunable constants
└── WavIO.cs                  # Interleaved float I/O helpers
```

---

## License

MIT
