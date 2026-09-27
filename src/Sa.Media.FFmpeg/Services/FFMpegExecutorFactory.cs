using Microsoft.Extensions.Logging;

namespace Sa.Media.FFmpeg.Services;

internal sealed class FFMpegExecutorFactory(
    IFFMpegLocator? mpegLocator = null,
    IProcessExecutor? processExecutor = null,
    ILoggerFactory? loggerFactory = null) : IFFMpegExecutorFactory
{
    readonly IFFMpegLocator _mpegLocator = mpegLocator ?? new FFMpegLocator();

    // IProcessExecutor.Default — отдельный экземпляр; держать его в локальной переменной
    // значило бы плодить копии на каждый Create* вызов.
    readonly IProcessExecutor _processExecutor = processExecutor ?? IProcessExecutor.Default;

    public IFFMpegExecutor CreateFFMpegExecutor(FFMpegOptions? options = null)
    {
        options?.EnsureWritableDirectory();

        var executablePath = GetExecutablePath(options);
        var executor = new FFRawExecutor(_processExecutor, executablePath, GetTimeout(options));
        return new FFMpegExecutor(executor, options?.WritableDirectory);
    }

    public IFFProbeExecutor CreateFFProbeExecutor(FFMpegOptions? options = null)
    {
        // ffprobe обязан лежать рядом с тем ffmpeg, которым мы собираемся работать: иначе
        // в плеере окажется чужой ffprobe из PATH, собранный с другими библиотеками.
        var ffprobePath = _mpegLocator.FindFFprobeExecutablePath(GetExecutablePath(options));
        var executor = new FFRawExecutor(_processExecutor, ffprobePath, GetTimeout(options));
        return new FFProbeExecutor(executor, loggerFactory?.CreateLogger<FFProbeExecutor>());
    }

    string GetExecutablePath(FFMpegOptions? mpegOptions = null)
    {
        var executablePath = mpegOptions?.ExecutablePath ?? _mpegLocator.FindFFmpegExecutablePath();

        if (!File.Exists(executablePath))
            throw new FileNotFoundException(
                $"FFmpeg executable not found: '{executablePath}'. " +
                "Check FFMpegOptions:ExecutablePath, or install FFmpeg system-wide.",
                executablePath);

        return executablePath;
    }

    static TimeSpan GetTimeout(FFMpegOptions? options) =>
        options?.Timeout is { } timeout && timeout > TimeSpan.Zero ? timeout : Constants.DefaultTimeout;
}
