using Sa.Classes;
using System.Diagnostics;
using System.Text;

namespace Sa.Media.FFmpeg.Services;

internal sealed class FFRawExtecutor(
    IProcessExecutor executor
    , string executablePath
    , TimeSpan timeout) : IFFRawExtecutor
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
        Action<ProcessStartInfo>? configure = null,
        CancellationToken cancellationToken = default)
    {
        var psi = GetStartInfo(ExecutablePath, commandArguments);
        configure?.Invoke(psi);

        return executor.ExecuteWithResultAsync(
            psi,
            captureErrorOutput,
            timeout ?? DefaultTimeout,
            cancellationToken);
    }

    public Task ExecuteStdOutAsync(
        string commandArguments,
        Stream inputStream,
        Func<Stream, CancellationToken, Task> onOutput,
        TimeSpan? timeout = null,
        Action<ProcessStartInfo>? configure = null,
        CancellationToken cancellationToken = default)
    {
        var psi = GetStartInfo(ExecutablePath, commandArguments);
        configure?.Invoke(psi);
        return executor.ExecuteStdOutAsync(psi, inputStream, onOutput, timeout, cancellationToken);
    }
}
