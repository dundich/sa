# Sa.Media.FFmpeg

Cross-platform .NET wrapper for FFmpeg (Windows x64, Linux x64) with **bundled fully-static binaries** — works out of the box without system-wide installation. Simplifies audio processing: metadata extraction, format conversion, channel split/join, and DI integration.

---

## Features

- 🎵 **Metadata extraction** — duration, bitrate, format, size via `ffprobe`
- 🔊 **Audio conversion** — PCM S16 LE WAV, PCM S32 LE WAV, raw PCM S16 LE/F32 LE binary, MP3, OGG Vorbis/Opus
- 🎛️ **Channel manipulation** — split stereo to mono files, join two monos into stereo (via DI)
- 📦 **Bundled FFmpeg binaries** — fully-static builds for `win-x64` and `linux-x64`, extracted into the app's output directory at build time (no system install, no native dependencies)
- 💉 **DI support** — standard `IServiceCollection` integration with options configuration
- ⚡ **Streaming I/O** — pipe audio directly from streams without intermediate files

---

## Quick Start

### Default instances (no setup required)

```csharp
using Sa.Media.FFmpeg;

// Metadata extraction
var meta = await IFFProbeExecutor.Default.GetMetaInfo("input.mp3");
Console.WriteLine($"Duration: {meta.Duration}s, Format: {meta.FormatName}");

// Get channels and sample rate separately
var (channels, sampleRate) = await IFFProbeExecutor.Default.GetChannelsAndSampleRate("input.mp3");

// Audio conversion
await IFFMpegExecutor.Default.ConvertToPcmS16Le(
    "input.mp3",
    "output.wav",
    outputSampleRate: 16000,
    outputChannelCount: 1);

// PCM S32 LE WAV conversion
await IFFMpegExecutor.Default.ConvertToPcmS32Le(
    "input.mp3",
    "output_s32le.wav",
    outputSampleRate: 48000,
    outputChannelCount: 2);

// PCM F32 LE WAV conversion (float samples with WAV header)
await IFFMpegExecutor.Default.ConvertToPcmF32Le(
    "input.mp3",
    "output_f32le.wav",
    outputSampleRate: 48000,
    outputChannelCount: 2);

// Raw float32 LE binary
await IFFMpegExecutor.Default.ConvertToPcmF32LeRaw(
    "input.mp3",
    "output_raw.f32le",
    outputSampleRate: 48000,
    outputChannelCount: 2);

// Raw PCM S16 LE binary (no WAV header)
await IFFMpegExecutor.Default.ConvertToPcmS16LeRaw(
    "input.mp3",
    "output_raw.s16le",
    outputSampleRate: 16000,
    outputChannelCount: 1);

// Convert preserving original sample rate and channels
await IFFMpegExecutor.Default.ConvertToPcmS16LePreservingFormat(
    "input.mp3",
    "output_preserved.wav");

// MP3 conversion
await IFFMpegExecutor.Default.ConvertToMp3("input.wav", "output.mp3");

// OGG Vorbis conversion
await IFFMpegExecutor.Default.ConvertToOgg("input.wav", "output.ogg", isLibopus: false);

// OGG Opus conversion
await IFFMpegExecutor.Default.ConvertToOgg("input.wav", "output.opus", isLibopus: true);

// Get supported formats/codecs
var formats = await IFFMpegExecutor.Default.GetFormats();
var codecs  = await IFFMpegExecutor.Default.GetCodecs();

// FFmpeg version
var version = await IFFMpegExecutor.Default.GetVersion();
```

### Channel split (stereo → mono files)

Resolve via DI (the class is internal):

```csharp
// After calling builder.Services.AddSaFFMpeg(...):
var services = builder.Services.BuildServiceProvider();
var splitter = services.GetRequiredService<IPcmS16LeChannelManipulator>();

var resultFiles = await splitter.SplitAsync(
    inputFileName: "stereo.mp3",
    outputFileName: "output",
    outputSampleRate: 16000,
    isOverwrite: true);

// Produces:
//   output_channel_0.wav  — left channel
//   output_channel_1.wav  — right channel
```

### Channel join (mono → stereo)

```csharp
var merger = services.GetRequiredService<IPcmS16LeChannelManipulator>();

var joined = await merger.JoinAsync(
    leftFileName: "left.wav",
    rightFileName: "right.wav",
    outputFileName: "stereo_output.wav",
    outputSampleRate: 16000);
```

### Streaming conversion (no intermediate files)

```csharp
await using var inputStream = File.OpenRead("input.mp3");

await IFFMpegExecutor.Default.ConvertToPcmS16Le(
    inputStream,
    inputFormat: "mp3",
    onOutput: async (stream, ct) =>
    {
        // Process WAV stream directly — e.g., feed into AsyncWavReader
        await using var reader = new AsyncWavReader(stream);
        await foreach (var packet in reader.ReadDoubleSamplesAsync(ct))
        {
            Console.WriteLine($"Sample: {packet.Sample:F4}");
        }
    },
    outputSampleRate: 16000,
    outputChannelCount: 1);
```

### Raw streaming conversion

```csharp
await using var inputStream = File.OpenRead("input.mp3");

await IFFMpegExecutor.Default.ConvertToPcmF32LeRaw(
    inputStream,
    inputFormat: "mp3",
    onOutput: async (rawStream, ct) =>
    {
        // Read raw f32le binary — each float is 4 bytes
        var buffer = new byte[4096];
        while (true)
        {
            var read = await rawStream.ReadAsync(buffer, ct);
            if (read == 0) break;
            // Process raw float32 samples...
        }
    },
    outputSampleRate: 48000,
    outputChannelCount: 2);
```

### Stream-based metadata extraction

```csharp
await using var stream = File.OpenRead("input.mp3");

var meta = await IFFProbeExecutor.Default.GetMetaInfo(stream, inputFormat: "mp3");
Console.WriteLine($"Duration: {meta.Duration}s, Bitrate: {meta.BitRate} bps");
```

---

## With DI

`AddSaFFMpeg` registers FFmpeg through the standard `Microsoft.Extensions.Options` pipeline and
returns the `IServiceCollection`, so it composes with the other `Add...` calls. The `configure`
callback receives the `IFFMpegBuilder`: the section comes from `FromConfiguration("…")`, the
options pipeline from `Options(...)` — which gets the `OptionsBuilder<FFMpegOptions>`:

```csharp
builder.Services.AddSaFFMpeg(o => o.Options(ob => ob.Configure(options =>
{
    options.ExecutablePath = @"C:\tools\ffmpeg.exe"; // optional override
    options.WritableDirectory = @"C:\temp\output";
    options.TimeoutSeconds = 300; // 5 minutes
})));

// Usage:
var sp = builder.Services.BuildServiceProvider();
var executor = sp.GetRequiredService<IFFMpegExecutor>();
var probe    = sp.GetRequiredService<IFFProbeExecutor>();
var manip    = sp.GetRequiredService<IPcmS16LeChannelManipulator>();
```

### Pre- and post-initialisation

The pipeline order is fixed — **`Configure` → `PostConfigure` → validation** — so a value
normalised in post-initialisation is what validation sees. `Configure` runs first and receives the
raw values; `PostConfigure` runs after every `Configure` and after the registration's own
normalisation, so anything you add there sees an already-normalised value:

```csharp
builder.Services.AddSaFFMpeg(o => o
    .Options(ob => ob.Configure(options => options.ExecutablePath = @"C:\tools\ffmpeg.exe")
    .PostConfigure(options => options.WritableDirectory ??= Path.GetTempPath())
    .Validate(options => options.TimeoutSeconds is null or >= 5, "TimeoutSeconds must be at least 5.")));
```

The registration's own `PostConfigure` resolves `ExecutablePath` and `WritableDirectory` to full
paths and turns a blank value into "unset". The `Options(...)` actions replay after the section
binding and the registration's own checks, so their `Configure` runs last and their `Validate`
adds to — rather than replaces — the built-in checks.

### Configuration section binding

Pass the section via `FromConfiguration` and the options are bound from `IConfiguration`:

```csharp
builder.Services.AddSaFFMpeg(b => b.FromConfiguration("Ffmpeg"));

// appsettings.json:
// {
//   "Ffmpeg": {
//     "ExecutablePath": "/usr/bin/ffmpeg",
//     "WritableDirectory": "/tmp/output",
//     "TimeoutSeconds": 300
//   }
// }
```

The section binds in a fixed slot **before** the `Options(...)` actions replay, so their
`Configure` has the last word when both are used.

### One registration per collection

`AddSaFFMpeg` owns the unnamed `FFMpegOptions` instance, so a second call throws
`InvalidOperationException` — two registrations would stack both `Configure` callbacks over the
same options and silently merge the settings.

---

## Supported Conversions

| Source | Target | Method | Notes |
|--------|--------|--------|-------|
| Any FFmpeg-supported | **PCM S16 LE WAV** | `ConvertToPcmS16Le()` | Custom sample rate (default 16 kHz), channel count |
| Any | **PCM S16 LE WAV** | `ConvertToPcmS16LePreservingFormat()` | Preserves original sample rate & channels |
| Any | **Raw PCM S16 LE binary** | `ConvertToPcmS16LeRaw()` | No WAV header, custom sample rate/channels |
| Any | **PCM S32 LE WAV** | `ConvertToPcmS32Le()` | 32-bit signed integer, custom sample rate/channels |
| Any | **PCM F32 LE WAV** | `ConvertToPcmF32Le()` | 32-bit IEEE float, custom sample rate/channels |
| Any | **Raw PCM F32 LE binary** | `ConvertToPcmF32LeRaw()` | 32-bit IEEE float, no WAV header |
| Any | **MP3** | `ConvertToMp3()` | 16 kHz, 128 kbps, libmp3lame |
| Any | **OGG Vorbis** | `ConvertToOgg(isLibopus: false)` | Standard Vorbis codec |
| Any | **OGG Opus** | `ConvertToOgg(isLibopus: true)` | Opus codec |

---

## Settings

### FFMpegOptions

| Property | Type | Description | Default |
|----------|------|-------------|---------|
| `ExecutablePath` | `string?` | Full path to ffmpeg/ffprobe binary | Auto-discovery (bundled → PATH) |
| `WritableDirectory` | `string?` | Default directory for bare output file names (names without a directory part); created if missing | Unset — bare names stay relative to the process working directory |
| `TimeoutSeconds` | `int?` | Operation timeout in seconds | `300` (5 minutes) |

Options are validated at host start (`ValidateOnStart`): `TimeoutSeconds` must be non-negative or unset, and `WritableDirectory` must not point at an existing *file* — a missing directory is fine, the factory creates it.

---

## Public API Reference

### IFFMpegExecutor

| Property/Method | Returns | Description |
|-----------------|---------|-------------|
| `Default` | `IFFMpegExecutor` | Static default instance (uses bundled binary) |
| `Executor` | `IFFRawExecutor` | Underlying raw process executor |
| `GetVersion()` | `Task<string>` | FFmpeg version string |
| `GetFormats()` | `Task<string>` | All supported formats |
| `GetCodecs()` | `Task<string>` | All supported codecs |
| `ConvertToPcmS16Le(file, file, ...)` | `Task<string>` | Convert to WAV file (PCM S16 LE) |
| `ConvertToPcmS16Le(stream, func, ...)` | `Task` | Stream-based conversion |
| `ConvertToPcmS16LePreservingFormat(file, file, ...)` | `Task<string>` | Convert preserving original format |
| `ConvertToPcmS16LeRaw(file, file, ...)` | `Task<string>` | Convert to raw s16le binary file |
| `ConvertToPcmS16LeRaw(stream, func, ...)` | `Task` | Stream-based raw s16le conversion |
| `ConvertToPcmS32Le(file, file, ...)` | `Task<string>` | Convert to WAV file (PCM S32 LE) |
| `ConvertToPcmF32Le(file, file, ...)` | `Task<string>` | Convert to WAV file (PCM F32 LE) |
| `ConvertToPcmF32LeRaw(file, file, ...)` | `Task<string>` | Convert to raw f32le binary file |
| `ConvertToPcmF32LeRaw(stream, func, ...)` | `Task` | Stream-based raw f32le conversion |
| `ConvertToMp3(file, file, ...)` | `Task<string>` | Convert to MP3 |
| `ConvertToOgg(file, file, ...)` | `Task<string>` | Convert to OGG (Vorbis or Opus) |

### IFFProbeExecutor

| Property/Method | Returns | Description |
|-----------------|---------|-------------|
| `Default` | `IFFProbeExecutor` | Static default instance |
| `Executor` | `IFFRawExecutor` | Underlying raw process executor |
| `GetChannelsAndSampleRate()` | `Task<(int? channels, int? sampleRate)>` | Raw channel/sample-rate pair |
| `GetMetaInfo(file)` | `Task<MediaMetadata>` | Full metadata from file path |
| `GetMetaInfo(stream, format)` | `Task<MediaMetadata>` | Full metadata from stream |

### IPcmS16LeChannelManipulator

| Method | Returns | Description |
|--------|---------|-------------|
| `SplitAsync(input, output, ...)` | `Task<IReadOnlyList<string>>` | Split stereo → separate mono WAVs (naming: `{base}_channel_{N}.{ext}`) |
| `JoinAsync(left, right, output, ...)` | `Task<string>` | Join two monos → stereo WAV |

### IFFRawExecutor

| Property/Method | Returns | Description |
|-----------------|---------|-------------|
| `ExecutablePath` | `string` | Path to the ffmpeg binary |
| `DefaultTimeout` | `TimeSpan` | Default operation timeout |
| `ExecuteAsync(args, ...)` | `Task<ProcessExecutionResult>` | Execute FFmpeg with arguments |
| `ExecuteStdOutAsync(args, stream, func, ...)` | `Task` | Stream stdin/stdout through FFmpeg |

---

## Domain Types

### MediaMetadata

```csharp
public sealed record MediaMetadata(
    double? Duration = null,
    string? FormatName = null,
    int? BitRate = null,
    long? Size = null)
{
    public static readonly MediaMetadata Empty = new();
}
```

> `Size` is `long?` (bytes): `int` overflowed on files over 2 GB, and ffprobe reported garbage for them.

### ProcessExecutionResult

```csharp
public record ProcessExecutionResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);
```

---

## Exceptions

| Exception | Namespace | When thrown |
|-----------|-----------|------------|
| `ProcessExecutionException` | `Sa.Media.FFmpeg.Services` | FFmpeg exits with non-zero code |
| `ProcessExecutionResultException` | `Sa.Media.FFmpeg.Services` | Wraps `ProcessExecutionResult` with formatted message |
| `ProcessStartException` | `Sa.Media.FFmpeg.Services` | Failed to start FFmpeg process |
| `ProcessTimeoutException` | `Sa.Media.FFmpeg.Services` | Operation exceeded timeout |

---

## Bundled Binaries

FFmpeg static builds are packed into the NuGet package and extracted by an MSBuild target into `sa/native/` next to the application at build time. No system installation required.

**Supported RIDs:** `win-x64`, `win-arm64`, `linux-x64`. **Payloads shipped in the package:** `win-x64` and `linux-x64`. `win-arm64` has no payload yet — the build emits a loud warning, and at runtime the library falls back to a system FFmpeg (PATH or `ExecutablePath`). Other platforms (Linux arm64, macOS) resolve to no payload as well.

**Discovery order:**
1. `AppContext.BaseDirectory/ffmpeg`
2. `AppContext.BaseDirectory/sa/native/ffmpeg`
3. System `PATH`, plus common install dirs (`Program Files\ffmpeg\bin` on Windows; `/usr/local/bin`, `/usr/bin`, `/bin` on Unix)

---

## License

MIT
