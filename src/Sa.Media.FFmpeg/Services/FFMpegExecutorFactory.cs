using Sa.Classes;

namespace Sa.Media.FFmpeg.Services;

internal class FFMpegExecutorFactory(
    IFFMpegLocator? mpegLocator = null
    , IProcessExecutor? processExecutor = null) : IFFMpegExecutorFactory
{
    public IFFMpegExecutor CreateFFMpegExecutor(FFMpegOptions? options = null)
    {
        var executablePath = GetExecutablePath(options);
        var executor = new FFMpegProcessExteсutor(
            processExecutor ?? IProcessExecutor.Default
            , executablePath
            , options?.Timeout ?? Constants.DefaultTimeout);
        return new FFMpegExecutor(executor);
    }

    public IFFProbeExecutor CreateFFProbeExecutor(FFMpegOptions? options = null)
    {
        var ffmpegPath = GetExecutablePath(options);

        var directory = Path.GetDirectoryName(ffmpegPath)
            ?? throw new ArgumentException("Directory not found");

        var ffprobePath = Path.Combine(directory, Constants.FFprobeExecutableFileName);

        var executor = new FFMpegProcessExteсutor(
            processExecutor ?? IProcessExecutor.Default
            , ffprobePath
            , options?.Timeout ?? Constants.DefaultTimeout);

        return new FFProbeExecutor(executor);
    }

    private string GetExecutablePath(FFMpegOptions? mpegOptions = null)
    {
        mpegLocator ??= new FFMpegLocator();

        var executablePath = mpegOptions?.ExecutablePath
            ?? mpegLocator.FindFFmpegExecutablePath(mpegOptions?.WritableDirectory);

        if (!File.Exists(executablePath))
            throw new FileNotFoundException("FFmpeg executable not found", executablePath);

        return executablePath;
    }
}
