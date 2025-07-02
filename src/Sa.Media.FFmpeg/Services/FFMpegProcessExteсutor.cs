using Sa.Classes;
using System.Diagnostics;
using System.Text;

namespace Sa.Media.FFmpeg.Services;

internal class FFMpegProcessExteсutor(
    IProcessExecutor executor
    , string executablePath
    , TimeSpan timeout) : IFFMpegProcessExteсutor
{
    public string ExecutablePath => executablePath;

    public TimeSpan DefaultTimeout => timeout;

    private static ProcessStartInfo GetStartInfo(string path, string arguments)
    {
        return new ProcessStartInfo
        {
            FileName = path,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
    }

    public Task<ProcessExecutionResult> ExecuteAsync(
        string commandArguments,
        bool captureErrorOutput = false,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        return executor.ExecuteWithResultAsync(
            GetStartInfo(ExecutablePath, commandArguments),
            captureErrorOutput,
            timeout ?? DefaultTimeout,
            cancellationToken);
    }
}
