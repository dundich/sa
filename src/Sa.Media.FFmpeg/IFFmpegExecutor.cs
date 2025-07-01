using Sa.Classes;

namespace Sa.Media.FFmpeg;

public interface IFFmpegExecutor
{
    string FFmpegExecutablePath { get; }

    Task<string> GetVersion(CancellationToken cancellationToken = default);
    
    Task<ProcessExecutionResult> ExecuteFFmpegAsync(
        string commandArguments, 
        bool captureErrorOutput = false, 
        TimeSpan? timeout = null, 
        CancellationToken cancellationToken = default);

    Task<ProcessExecutionResult> ExecuteFFprobeAsync(
        string commandArguments,
        bool captureErrorOutput = false,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    static IFFmpegExecutor Default { get; } = FFMpegExecutor.Default;
}
