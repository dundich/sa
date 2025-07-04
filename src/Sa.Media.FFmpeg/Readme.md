#  Sa.Media.FFmpeg

## FFmpeg .NET Wrapper - ready to use out of the box with minimal setup

A cross-platform .NET wrapper for FFmpeg (Windows x64 and Linux), designed to simplify audio and video processing in .NET applications. The library provides a bundled static FFmpeg build when it is not installed system-wide, ensuring smooth operation without external dependencies.

- Extract metadata from media files (duration, channels, sample rate, etc.)
- Convert audio to: WAV, MP3, OGG ..
- Built-in FFmpeg binaries for Windows x64 and Linux
- Supports Dependency Injection (DI) via standard IServiceCollection integration


Interfaces:

- `IFFMpegExecutor` — perform audio conversion tasks by `ffmpeg`
- `IFFProbeExecutor` — retrieve stream info and metadata by `ffprobe`


## Example Usage

Audio Conversion to MP3

```csharp

using var host = Host.CreateDefaultBuilder()
    .ConfigureServices(services =>
    {
        services.AddFFMpeg(); // Registers IFFMpegExecutor and IFFProbeExecutor
    })
    .Build();

var ffmpeg = host.Services.GetRequiredService<IFFMpegExecutor>();

await ffmpeg.ConvertToMp3("input.wav", "output.mp3");
```
