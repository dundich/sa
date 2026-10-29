#  Example with Sa.Media.FFmpeg

Program.cs

```csharp
var ffmpeg = Sa.Media.FFmpeg.IFFMpegExecutor.Default;

var ver = await ffmpeg.GetVersion();
Console.WriteLine(ver.Split('\n')[0].TrimEnd('\r'));

var dataDir = Path.Combine(AppContext.BaseDirectory, "data");

await ffmpeg.ConvertToPcmS16Le(
    Path.Combine(dataDir, "input.mp3"),
    Path.Combine(dataDir, "output.wav"),
    outputChannelCount: 1);
```

> **Why `ProjectReference` and not `PackageReference`.** The sample deliberately references
> `Sa.Media.FFmpeg.csproj`. The local feed `src/nupkgs` is empty in git and `packageSourceMapping`
> in `src/nuget.config` allows `Sa.*` only from it, so a `PackageReference` breaks
> `dotnet restore src/Sa.slnx` on a fresh clone (NU1101). The packaged path
> (`buildTransitive/Sa.Media.FFmpeg.targets`) is covered by `./build-sh/do-package-check.sh` in CI.

Nothing has to be installed, on Windows x64 or on Linux x64 (musl/Alpine included): the bundled
FFmpeg is fully static — `ldd sa/native/ffmpeg` answers *not a dynamic executable* and
`readelf -d` has no `NEEDED` entries, because the codecs (`libmp3lame`, `libopus`, `libvorbis`)
and libc are linked in.

> ⚠️ There is no "bundle failed → fall back to the system ffmpeg" path: `IFFMpegLocator` takes the
> first *existing* file. A `sa/native/ffmpeg` that exists but cannot run (corrupt payload, `noexec`
> mount, wrong platform) is reported instead of being skipped — `GetVersion()`/`GetCodecs()` throw
> `ProcessExecutionResultException` with ffmpeg's exit code and stderr tail, so the cause is visible
> immediately. To use your own build, set `FFMpegOptions.ExecutablePath`.


wsl build
```
dotnet nuget locals all --clear
dotnet restore -r linux-x64
dotnet build -c Debug -r linux-x64

# dotnet run
```
