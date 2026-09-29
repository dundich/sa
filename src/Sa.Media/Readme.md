# Sa.Media

Async, memory-efficient WAV file reader for .NET 10+. Designed for Native AOT compatibility with zero allocations on hot paths. Includes a stereo cross-feed suppression pipeline (`Sa.Media.Echo`) for separating closely miked sources.

---

## Features

- **Fully asynchronous** — `PipeReader`-based streaming, no blocking I/O
- **Memory efficient** — `ArrayPool`/`MemoryPool` buffer reuse, minimal GC pressure
- **Multi-format support** — PCM 8/16/24/32-bit, IEEE Float 32/64-bit
- **Extensible** — supports `WAVE_FORMAT_EXTENSIBLE` chunks
- **Time-based trimming** — read only the portion you need via `TimeRange`
- **Channel-aware** — per-channel sample enumeration with position tracking
- **Automatic chunk skipping** — `JUNK`, `LIST`, and other metadata chunks are transparently skipped
- **Cross-feed suppression** — stereo separation pipeline in `Sa.Media.Echo`

---

## Quick Start

### Read header

```csharp
using var stream = File.OpenRead("test.wav");
var reader = new AsyncWavReader(stream);

var header = await reader.GetHeaderAsync();
Console.WriteLine($"{header.NumChannels}ch @ {header.SampleRate}Hz, " +
    $"{header.BitsPerSample}-bit {header.AudioFormat}");
```

### Read raw samples per channel

```csharp
await using var reader = AsyncWavReader.CreateFromFile("test.wav");

await foreach (var packet in reader.ReadSamplesPerChannelAsync(
    cancellationToken: ct))
{
    Console.WriteLine($"Ch#{packet.ChannelId}: {packet.Sample.Length} bytes at pos {packet.Position}");
}
```

### Read normalized double samples [-1.0 … 1.0]

```csharp
await using var reader = AsyncWavReader.CreateFromFile("test.wav");

await foreach (var packet in reader.ReadDoubleSamplesAsync(cancellationToken: ct))
{
    Console.WriteLine($"Ch#{packet.ChannelId}: {packet.Sample:F4}");
}
```

### Streamable batches (ideal for audio pipelines)

```csharp
await using var reader = AsyncWavReader.CreateFromFile("test.wav");

await foreach (var batch in reader.ReadStreamableChunksAsync(
    samplesPerBatch: 4096,
    cancellationToken: ct))
{
    // Each yield produces independent data — safe to process asynchronously
}
```

### Trim by time range

```csharp
await using var reader = AsyncWavReader.CreateFromFile("test.wav");

// Read only seconds 5–15
var range = TimeRange.Seconds(5, 15);
await foreach (var packet in reader.ReadDoubleSamplesAsync(range, cancellationToken: ct))
{
    // Samples from the trimmed range only
}
```

### Convert to different format

```csharp
await using var reader = AsyncWavReader.CreateFromFile("input.wav");

// Convert to 24-bit PCM
await foreach (var packet in reader.ConvertToFormatAsync(
    AudioEncoding.Pcm24BitSigned,
    cancellationToken: ct))
{
    // Raw 24-bit PCM bytes per sample
}
```

---

## Cross-feed suppression (`Sa.Media.Echo`)

Separates a stereo recording of two closely miked speakers/microphones into two
near-dialectal tracks. The algorithm estimates the acoustic cross-talk between
channels (`y_L = x_L + β·x_R`, `y_R = x_R + α·x_L`) and inverts the model.

Supports two modes:

- **Linear** — cross-talk cancellation only (fast, low memory).
- **Aggressive** — cross-talk cancellation plus per-frequency spectral masking
  for sharper separation (higher CPU/GPU cost).

```csharp
using Sa.Media.Echo;

// Automatic mode: in-memory for short clips, streaming for long ones
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

The separator must run on a **2-channel (stereo)** recording. Output is 16-bit
PCM WAV, peak-normalized. The linear path streams and avoids loading the whole
file into memory; the in-memory path kicks in automatically for short
recordings (≤ 100 Mframes).

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

All formats support mono and stereo. Unknown chunks (`JUNK`, `LIST`, etc.) are automatically skipped.

---

## Public API Reference

### Core types

| Type | Description |
|------|-------------|
| `AsyncWavReader` | Main async WAV reader — creates from `Stream` or file path |
| `WavHeader` | Parsed RIFF/WAV header with computed properties (`IsPcm`, `IsStereo`, `Duration`) |
| `AudioPacket` | Record: `(ChannelId, Sample, Position, IsEof)` — raw/conversion bytes |
| `AudioNormalizedPacket` | Record: `(ChannelId, Sample, Position, IsEof)` — normalized double [-1.0, 1.0] |
| `TimeRange` | Record: `(From, To)` — time-based trimming with factory methods |
| `AudioEncoding` | Enum: PCM 8/16/24/32, IEEE Float 32/64 |
| `WaveFormatType` | Enum: `Pcm`, `Adpcm`, `IeeeFloat`, `Extensible` |

### Echo types (`Sa.Media.Echo`)

| Type | Description |
|------|-------------|
| `CrossFeedSeparator` | Stereo cross-feed suppression pipeline (entry point) |
| `CrossFeedSeparator.ProcessingMode` | Enum: `Auto`, `MaximumSpeed`, `MinimumMemory`, `MinimumMemoryFastNormalize`, `Aggressive` |
| `AudioSeparationOptions` | Record: `InputPath`, `OutputPath`, `Processing`, `DominanceThresholdDb`, `SpectralMaskPower`, `MaskFloor` |

### Key methods on `CrossFeedSeparator`

| Method | Returns | Description |
|--------|---------|-------------|
| `ExecuteAsync(AudioSeparationOptions, ct)` | `Task` | Runs separation and writes the output WAV |

### Key methods on `AsyncWavReader`

| Method | Returns | Description |
|--------|---------|-------------|
| `Create(Stream)` | `AsyncWavReader` | Factory from stream |
| `CreateFromFile(string)` | `AsyncWavReader` | Factory from file path |
| `GetHeaderAsync()` | `Task<WavHeader>` | Thread-safe lazy header parsing |
| `ReadSamplesPerChannelAsync()` | `IAsyncEnumerable<AudioPacket>` | Raw samples per channel |
| `ReadDoubleSamplesAsync()` | `IAsyncEnumerable<AudioNormalizedPacket>` | Normalized double samples |
| `ConvertToFormatAsync()` | `IAsyncEnumerable<AudioPacket>` | Convert to target encoding |
| `ReadStreamableChunksAsync()` | `IAsyncEnumerable<AudioPacket>` | Batched samples for pipelines |

### `TimeRange` factories

| Method | Example | Description |
|--------|---------|-------------|
| `TimeRange.Create(from, to)` | Basic constructor | From/to TimeSpan |
| `TimeRange.Ms(from, to)` | By milliseconds | Millisecond precision |
| `TimeRange.Seconds(from, to)` | By seconds | Double-second precision |
| `TimeRange.RangeFromDuration(from, dur)` | From start + duration | Build from offset |
| `TimeRange.Default` | `[0, ∞)` | Full file, no trim |

---

## Performance Notes

- `allowBufferReuse=true` (default) reuses pooled buffers across yields — caller must copy before next iteration
- `allowBufferReuse=false` allocates a fresh array per sample — safer for parallel consumers
- `ReadStreamableChunksAsync` forces `allowBufferReuse:false` internally to prevent buffer aliasing
- All internal awaits use `ConfigureAwait(false)` — safe in any synchronization context

---

## Project Layout

```
src/Sa.Media/
├── AsyncWavReader.cs        # Main reader class
├── AsyncWavWriter.cs        # Internal WAV writer
├── AudioEncoding.cs         # Format enum
├── AudioEncodingExtensions.cs
├── AudioPacket.cs           # Raw sample record
├── AudioNormalizedPacket.cs # Normalized sample record
├── BinaryPipeReader.cs      # Little-endian binary reader
├── PipeReaderExtensions.cs  # Skip helpers
├── SampleConverter.cs       # PCM ↔ double conversion
├── TimeRange.cs             # Trimming range
├── TimeRangeExtensions.cs   # Expander, merge, sort
├── WavHeader.cs             # RIFF header model
├── WavHeaderReader.cs       # Header parser
├── WaveFormatType.cs        # Format type enum
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
