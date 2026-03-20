#  Example with Sa.Media.FFmpeg

Program.cs

```csharp
var ffmpeg = Sa.Media.FFmpeg.IFFMpegExecutor.Default;

var ver = await ffmpeg.GetVersion();
Console.WriteLine(ver.AsSpan(0, 21));

await ffmpeg.ConvertToPcmS16Le(
    "data/input.mp3",
    "data/output.wav",
    outputChannelCount: 1);
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


wsl build
```
dotnet nuget locals all --clear
dotnet restore -r linux-x64
dotnet build -c Debug -r linux-x64

# dotnet run
```
