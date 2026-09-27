using System.Diagnostics;
using System.Text;

namespace Sa.Media.FFmpeg.Services;

internal interface IProcessExecutor
{
    /// <summary>Сколько символов stderr уходит в <see cref="ProcessExecutionResult.StandardError"/> и в текст исключения.</summary>
    internal const int StandardErrorTailLimit = 64 * 1024;

    /// <summary>
    /// Executes a process with real-time output handling via data received events.
    /// </summary>
    /// <param name="startInfo">Process start configuration.</param>
    /// <param name="outputDataReceived">Callback for stdout lines. If <c>null</c>, stdout is not redirected.</param>
    /// <param name="errorDataReceived">Callback for stderr lines. If <c>null</c>, stderr is not redirected.</param>
    /// <param name="timeout">Operation timeout. Use <c>null</c> or <see cref="TimeSpan.Zero"/> for no timeout.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The process exit code.</returns>
    Task<int> ExecuteAsync(
        ProcessStartInfo startInfo
        , Action<string>? outputDataReceived = null
        , Action<string>? errorDataReceived = null
        , TimeSpan? timeout = null
        , CancellationToken cancellationToken = default);


    /// <summary>
    /// Executes a process and collects all output into a <see cref="ProcessExecutionResult"/>.
    /// Throws <see cref="ProcessExecutionResultException"/> if exit code is non-zero and <paramref name="throwOnError"/> is true.
    /// </summary>
    /// <param name="startInfo">Process start configuration.</param>
    /// <param name="throwOnError">If true, throws <see cref="ProcessExecutionResultException"/> on non-zero exit code. If false, the result is always returned.</param>
    /// <param name="timeout">Operation timeout. Use <c>null</c> or <see cref="TimeSpan.Zero"/> for no timeout.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    async Task<ProcessExecutionResult> ExecuteWithResultAsync(
        ProcessStartInfo startInfo
        , bool throwOnError = true
        , TimeSpan? timeout = null
        , CancellationToken cancellationToken = default)
    {
        var output = new StringBuilder();
        // stderr у FFmpeg может быть размером с сам файл: при -loglevel error и битом потоке
        // это сотни мегабайт. Копим только хвост — в сообщение об ошибке он и нужен.
        var error = new BoundedStringBuilder(StandardErrorTailLimit);

        int exitcode;
        try
        {
            exitcode = await ExecuteAsync(
                startInfo
                , s => output.AppendLine(s)
                , e => error.AppendLine(e)
                , timeout
                , cancellationToken
            ).ConfigureAwait(false);
        }
        catch (ProcessTimeoutException ex)
        {
            // Прокручиваем дальше с stderr, накопленным до дедлайна: для зависшего процесса
            // хвост часто — единственная зацепка, на чём он застрял.
            throw new ProcessTimeoutException(
                error.Length > 0 ? $"{ex.Message}{Environment.NewLine}{error}" : ex.Message,
                ex);
        }

        var result = new ProcessExecutionResult(
            exitcode,
            StandardOutput: output.ToString(),
            StandardError: error.ToString());

        if (result.ExitCode == 0 || !throwOnError) return result;

        throw new ProcessExecutionResultException(result, ProcessExecutor.Describe(startInfo));
    }

    /// <summary>
    /// Executes a process and streams stdout through a callback. Stderr is collected and checked on completion.
    /// The input stream is copied to stdin asynchronously, then stdin is closed automatically.
    /// The caller's <paramref name="inputStream"/> is NOT disposed — it stays the caller's responsibility.
    /// </summary>
    /// <param name="startInfo">Process start configuration.</param>
    /// <param name="inputStream">Readable stream to copy to stdin.</param>
    /// <param name="onOutput">Callback that receives the stdout stream. Must read until EOF.</param>
    /// <param name="timeout">Operation timeout. Use <c>null</c> or <see cref="TimeSpan.Zero"/> for no timeout.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ProcessExecutionException">Thrown when the process returns a non-zero exit code.</exception>
    /// <exception cref="ProcessTimeoutException">Thrown on our own timeout. Never thrown for caller cancellation.</exception>
    Task ExecuteStdOutAsync(
        ProcessStartInfo startInfo
        , Stream inputStream
        , Func<Stream, CancellationToken, Task> onOutput
        , TimeSpan? timeout = null
        , CancellationToken cancellationToken = default);


    static IProcessExecutor Default { get; } = new ProcessExecutor();
}
