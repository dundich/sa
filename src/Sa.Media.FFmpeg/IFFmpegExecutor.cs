using Sa.Classes;

namespace Sa.Media.FFmpeg;

public interface IFFmpegExecutor
{
    string ExecutablePath { get; }

    Task<string> GetVersion(CancellationToken cancellationToken = default);
    
    Task<ProcessExecutionResult> ExecuteAsync(
        string commandArguments, 
        bool captureErrorOutput = true, 
        TimeSpan? timeout = null, 
        CancellationToken cancellationToken = default);

    static IFFmpegExecutor Default { get; } = FFMpegExecutor.Default;
}
