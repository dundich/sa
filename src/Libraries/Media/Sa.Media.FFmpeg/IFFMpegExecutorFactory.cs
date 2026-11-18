namespace Sa.Media.FFmpeg;

/// <summary>
/// Creates FFmpeg and FFprobe executors configured with the supplied options.
/// </summary>
public interface IFFMpegExecutorFactory
{
    /// <summary>
    /// Creates an executor for running FFmpeg commands.
    /// </summary>
    /// <param name="options">Optional options to use; defaults are used when <c>null</c>.</param>
    /// <returns>A configured FFmpeg executor.</returns>
    IFFMpegExecutor CreateFFMpegExecutor(FFMpegOptions? options = null);

    /// <summary>
    /// Creates an executor for running FFprobe commands.
    /// </summary>
    /// <param name="options">Optional options to use; defaults are used when <c>null</c>.</param>
    /// <returns>A configured FFprobe executor.</returns>
    IFFProbeExecutor CreateFFProbeExecutor(FFMpegOptions? options = null);
}
