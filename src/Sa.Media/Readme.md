# Sa.Media

Async, memory-efficient WAV file reader for .NET 10+. Designed for Native AOT compatibility with zero allocations on hot paths.

---

## Features

- **Fully asynchronous** — `PipeReader`-based streaming, no blocking I/O
- **Memory efficient** — `ArrayPool`/`MemoryPool` buffer reuse, minimal GC pressure
- **Multi-format support** — PCM 8/16/24/32-bit, IEEE Float 32/64-bit
- **Extensible** — supports `WAVE_FORMAT_EXTENSIBLE` chunks
- **Time-based trimming** — read only the portion you need via `TimeRange`
- **Channel-aware** — per-channel sample enumeration with position tracking
- **Automatic chunk skipping** — `JUNK`, `LIST`, and other metadata chunks are transparently skipped

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
```

---

## License

MIT
