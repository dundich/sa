
Console.WriteLine("Hello, [Sa.Media.FFmpeg]!");
var ffmpeg = Sa.Media.FFmpeg.IFFMpegExecutor.Default;

var ver = await ffmpeg.GetVersion();
Console.WriteLine(ver.AsSpan(0, 21));

var codecs = await ffmpeg.GetCodecs();
Console.WriteLine(codecs);

await ffmpeg.ConvertToPcmS16Le(
    "data/input.mp3",
    "data/output.wav",
    outputChannelCount: 1);
