using Sa.Classes;

namespace Sa.Media.FFmpeg;

public interface IFFProcessExteсutor
{
    string ExecutablePath { get; }
    TimeSpan DefaultTimeout { get; }
    Task<ProcessExecutionResult> ExecuteAsync(string commandArguments, bool captureErrorOutput = false, TimeSpan? timeout = null, CancellationToken cancellationToken = default);
}
