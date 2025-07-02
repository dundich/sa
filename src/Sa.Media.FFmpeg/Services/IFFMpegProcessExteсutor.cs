using Sa.Classes;

namespace Sa.Media.FFmpeg.Services;

internal interface IFFMpegProcessExteсutor
{
    string ExecutablePath { get; }
    TimeSpan DefaultTimeout { get; }
    Task<ProcessExecutionResult> ExecuteAsync(string commandArguments, bool captureErrorOutput = false, TimeSpan? timeout = null, CancellationToken cancellationToken = default);
}
