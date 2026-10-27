
Console.WriteLine("Hello, [Sa.Media.FFmpeg]!");
var ffmpeg = Sa.Media.FFmpeg.IFFMpegExecutor.Default;

var ver = await ffmpeg.GetVersion();
Console.WriteLine(ver.Split('\n')[0].TrimEnd('\r'));

var codecs = await ffmpeg.GetCodecs();
Console.WriteLine(codecs);

// data\ рядом с приложением (CopyToOutputDirectory), а не относительно текущего каталога:
// `dotnet run --project Samples/FFMpeg.Console` из src\ иначе искал бы src\data\input.mp3.
var dataDir = Path.Combine(AppContext.BaseDirectory, "data");

await ffmpeg.ConvertToPcmS16Le(
    Path.Combine(dataDir, "input.mp3"),
    Path.Combine(dataDir, "output.wav"),
    outputChannelCount: 1);
