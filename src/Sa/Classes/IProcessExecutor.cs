using System.Diagnostics;
using System.Text;

namespace Sa.Classes;

internal interface IProcessExecutor
{
    /// <summary>
    /// Executes a process with real-time output handling
    /// </summary>
    Task<int> ExecuteAsync(
        ProcessStartInfo startInfo
        , Action<string>? outputDataReceived = null
        , Action<string>? errorDataReceived = null
        , TimeSpan? timeout = null
        , CancellationToken cancellationToken = default);


    /// <summary>
    /// Executes a process and returns complete output
    /// </summary>
    async Task<ProcessExecutionResult> ExecuteWithResultAsync(
        ProcessStartInfo startInfo
        , bool captureErrorOutput = true
        , TimeSpan? timeout = null
        , CancellationToken cancellationToken = default)
    {
        var output = new StringBuilder();
        var error = new StringBuilder();

        var exitcode = await ExecuteAsync(
            startInfo
            , s => output.AppendLine(s)
            , e => error.AppendLine(e)
            , timeout
            , cancellationToken
        ).ConfigureAwait(false);

        var result = new ProcessExecutionResult(
            exitcode,
            StandardOutput: output.ToString(),
            StandardError: error.ToString());

        if (result.ExitCode == 0 || captureErrorOutput) return result;

        throw new ProcessExecutionResultException(result);
    }

    /// <summary>
    /// Executes stdout as a stream.
    /// Stderr is captured and checked on completion
    /// </summary>
    Task ExecuteStdOutAsync(
        ProcessStartInfo startInfo
        , Stream inputStream
        , Func<Stream, CancellationToken, Task> onOutput
        , TimeSpan? timeout = null
        , CancellationToken cancellationToken = default);


    static IProcessExecutor Default { get; } = new ProcessExecutor();
}

/// <summary>
/// Represents the result of a process execution, including exit code and output streams.
/// </summary>
public record ProcessExecutionResult(
    /// <summary>
    /// The exit code returned by the executed process.
    /// A value of 0 typically indicates success.
    /// </summary>
    int ExitCode,

    /// <summary>
    /// The standard output (stdout) captured from the process.
    /// </summary>
    string StandardOutput,

    /// <summary>
    /// The standard error (stderr) captured from the process.
    /// </summary>
    string StandardError);



internal sealed class ProcessExecutor : IProcessExecutor
{
    internal TimeSpan DefaultExecutionTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public async Task<int> ExecuteAsync(
        ProcessStartInfo startInfo
        , Action<string>? outputDataReceived = null
        , Action<string>? errorDataReceived = null
        , TimeSpan? timeout = null
        , CancellationToken cancellationToken = default)
    {

        cancellationToken.ThrowIfCancellationRequested();

        startInfo.RedirectStandardOutput = outputDataReceived != null;
        startInfo.RedirectStandardError = errorDataReceived != null;

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        if (!process.Start())
        {
            process.Dispose();
            throw new ProcessStartException($"Failed to start process: '{startInfo.FileName}' with arguments '{startInfo.Arguments}'");
        }

        var outputCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errorCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        int exitCode;

        try
        {
            if (outputDataReceived != null)
            {
                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data != null) outputDataReceived(e.Data);
                    else outputCompletion.TrySetResult(true);
                };
            }

            if (errorDataReceived != null)
            {
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null) errorDataReceived(e.Data);
                    else errorCompletion.TrySetResult(true);
                };
            }

            if (startInfo.RedirectStandardOutput)
                process.BeginOutputReadLine();

            if (startInfo.RedirectStandardError)
                process.BeginErrorReadLine();

            using var timeoutCts = new CancellationTokenSource(timeout ?? DefaultExecutionTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var waitTask = process.WaitForExitAsync(linkedCts.Token);

            // Wait for all streams to finish reading
            List<Task> outputTasks = [waitTask];

            if (outputDataReceived != null) outputTasks.Add(outputCompletion.Task);
            if (errorDataReceived != null) outputTasks.Add(errorCompletion.Task);

            await Task.WhenAll(outputTasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProcessTimeoutException("Process execution timed out");
        }
        catch (Exception ex)
        {
            throw new ProcessExecutionException(process.ExitCode, "Process execution failed", ex);
        }
        finally
        {
            exitCode = SafeDisposeProcess(process);
        }

        return exitCode;
    }

    /// <summary>
    /// Запускает процесс, и передаёт поток stdout в callback.
    /// Поток stderr собирается автоматически.
    /// При завершении — проверяется код возврата.
    /// </summary>
    /// <param name="startInfo">Настройки процесса.</param>
    /// <param name="inputStream">Поток для stdin</param>
    /// <param name="onOutput">Callback, получающий stdout. Должен быть асинхронным.</param>
    /// <returns>Задача, завершающаяся после обработки потока и проверки результата.</returns>
    public async Task ExecuteStdOutAsync(
        ProcessStartInfo startInfo,
        Stream inputStream,
        Func<Stream, CancellationToken, Task> onOutput,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Настройка
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;


        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };


        if (!process.Start())
        {
            process.Dispose();
            throw new ProcessStartException(
                $"Failed to start process: '{startInfo.FileName}' with arguments '{startInfo.Arguments}'");
        }

        StringBuilder stderrBuilder = new();
        int exitCode;
        try
        {
            // Создаём токен с таймаутом
            using var timeoutCts = timeout.HasValue
                ? new CancellationTokenSource(timeout.Value)
                : null;

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCts?.Token ?? CancellationToken.None);


            List<Task> backgroundTasks = [
                WriteToStdInAsync(process, inputStream, linkedCts.Token), // write stdin
                ReadStandardErrorToBuilderAsync(process, stderrBuilder, linkedCts.Token), // read stderr
            ];

            Stream stdoutStream = process.StandardOutput.BaseStream;
            await onOutput(stdoutStream, linkedCts.Token).ConfigureAwait(false);
            await stdoutStream.DisposeAsync();

            await Task.WhenAll(backgroundTasks).ConfigureAwait(false);
        }
        finally
        {
            exitCode = SafeDisposeProcess(process);
        }

        if (exitCode != 0)
        {
            throw new ProcessExecutionException(exitCode, $"Process failed (exit={exitCode}): {stderrBuilder}");
        }
    }


    private static async Task ReadStandardErrorToBuilderAsync(
        Process process,
        StringBuilder errorBuilder,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(error))
            {
                errorBuilder.Append(error);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (errorBuilder.Length == 0)
                errorBuilder.Append("stderr: read interrupted (cancellation).");
        }
        catch (Exception ex)
        {
            errorBuilder.Append($"stderr: read failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Асинхронно записывает входной поток в stdin процесса.
    /// Автоматически закрывает stdin после завершения.
    /// </summary>
    private static async Task WriteToStdInAsync(
        Process process,
        Stream inputStream,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // input to stdin
            await using (inputStream.ConfigureAwait(false))
            {
                await inputStream.CopyToAsync(process.StandardInput.BaseStream, cancellationToken)
                                 .ConfigureAwait(false);
            }

            // Завершаем запись
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            process.StandardInput.Close();
        }
        catch (Exception ex)
        {
            process.StandardInput.Close();
            throw new IOException("Failed to write input stream to process stdin.", ex);
        }
    }

    private static int SafeDisposeProcess(Process process)
    {
        int exitCode = -1;

        try
        {
            if (process.HasExited)
            {
                exitCode = process.ExitCode;
                return exitCode;
            }

            process.StandardInput?.Close();
            process.StandardOutput?.Close();

            // graceful shutdown
            if (process.WaitForExit(500))
            {
                exitCode = process.ExitCode;
                return exitCode;
            }

            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                if (process.HasExited)
                {
                    exitCode = process.ExitCode;
                    return exitCode;
                }
                throw;
            }
            catch (NotSupportedException)
            {
                process.Kill();
            }
            catch (UnauthorizedAccessException)
            {
                return exitCode;
            }

            if (process.WaitForExit(2000))
            {
                exitCode = process.ExitCode;
            }
            else
            {
                Console.WriteLine("Process did not terminate after Kill()");
            }
        }
        catch (InvalidOperationException)
        {
            Console.WriteLine("Process is invalid or already disposed.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Unexpected error during process termination: {ex.Message}");
        }
        finally
        {
            try
            {
                process.Dispose();
            }
            catch
            {
                // skeep
            }
        }

        return exitCode;
    }
}


// Custom exceptions
public class ProcessExecutionException(int exitCode, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public int Exitcode => exitCode;
}

public class ProcessExecutionResultException(ProcessExecutionResult result)
    : Exception($"Process failed (exit={result.ExitCode}): {result.StandardError}")
{
    public ProcessExecutionResult Result { get; } = result;
}

public class ProcessStartException(string message) : IOException(message)
{
}

public class ProcessTimeoutException(string message) : TimeoutException(message)
{
}
