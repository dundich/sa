internal class Program
{
    static async Task Main()
    {
        Console.WriteLine("Hello, World!");
        var ffmpeg = Sa.Media.FFmpeg.IFFMpegExecutor.Default;



        var ver = await ffmpeg.GetVersion();
        Console.WriteLine(ver);

        var codecs = await ffmpeg.GetCodecs();
        Console.WriteLine(codecs);
    }
}
