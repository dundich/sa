#  Sa.Media.FFmpeg

## FFmpeg .NET Wrapper - ready to use out of the box with minimal setup

A cross-platform .NET wrapper for FFmpeg (Windows x64 and Linux), designed to simplify audio and video processing in .NET applications. The library provides a bundled static FFmpeg build when it is not installed system-wide, ensuring smooth operation without external dependencies.

- Extract metadata from media files (duration, channels, sample rate, etc.)
- Convert audio to: wav, mp3, mp4, ogg, ac3, mov ..
- Splits/Join audio file by channels
- Built-in FFmpeg binaries for Windows x64 and Linux
- Supports Dependency Injection (DI) via standard IServiceCollection integration


Interfaces:

- `IFFMpegExecutor` — perform audio conversion tasks by `ffmpeg`
- `IFFProbeExecutor` — retrieve stream info and metadata by `ffprobe`


## Example Usage

Audio Conversion to MP3

```csharp

using var host = Host
    .CreateDefaultBuilder()
    .ConfigureServices(services => services.AddSaFFMpeg())
    .Build();

var ffmpeg = host.Services.GetRequiredService<IFFMpegExecutor>();

await ffmpeg.ConvertToMp3("input.wav", "output.mp3");
```


Splits input audio file by channels

```csharp
var splitter = new PcmS16LeChannelManipulator();

var resultFiles = await splitter.SplitAsync(
    inputFileName: "input.mp3",
    outputFileName: "output.wav",
    sampleRate: 16000,
    isOverwrite: true
);
```

This will produce:

```
output_channel_0.wav
output_channel_1.wav
```


## Check library dependencies
To see all missing dependencies:

```bash
cd bin/Debug/net9.0/runtimes/linux-x64/
ldd ffmpeg
```

On Ubuntu/Debian:

```bash
sudo apt update
sudo apt install libmp3lame0 libopus0 libvorbis0a libvorbisenc2
```

On Alpine Linux:

```bash
sudo apk add lame-libs opus libvorbis
```
