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
        , bool throwOnError = true
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

        if (result.ExitCode == 0 || !throwOnError) return result;

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


        int exitCode = await ExecuteProcessWithHandlersAsync(process, outputDataReceived, errorDataReceived, timeout, cancellationToken)
            .ConfigureAwait(false);

        return exitCode;
    }

    private static async Task<int> ExecuteProcessWithHandlersAsync(
        Process process,
        Action<string>? outputDataReceived,
        Action<string>? errorDataReceived,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        int exitCode;

        try
        {
            await Run(process, outputDataReceived, errorDataReceived, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Отмена вызывающим кодом. Порядок catch-блоков обязателен: если бы проверка таймаута
            // стояла первой, отмена пользователя превращалась бы в ProcessTimeoutException.
            throw;
        }
        catch (OperationCanceledException oce)
        {
            // Токен вызывающего не отменён — отменился только наш таймаут.
            throw new ProcessTimeoutException($"Process execution timed out after {Describe(timeout)}", oce);
        }
        catch (Exception ex)
        {
            // HasExited, а не process.ExitCode: у ещё живого процесса ExitCode бросает
            // InvalidOperationException, и в catch-блоке он бы заменил собой настоящую ошибку.
            throw new ProcessExecutionException(
                process.HasExited ? process.ExitCode : -1,
                $"Process execution failed: {Describe(process.StartInfo)}",
                ex);
        }
        finally
        {
            exitCode = SafeDisposeProcess(process);
        }

        return exitCode;
    }

    private static string Describe(ProcessStartInfo startInfo)
        => string.IsNullOrEmpty(startInfo.Arguments) ? startInfo.FileName : $"{startInfo.FileName} {startInfo.Arguments}";

    private static string Describe(TimeSpan? timeout) => timeout is { } t ? t.ToString() : "(none)";

    private static async Task Run(
        Process process,
        Action<string>? outputDataReceived,
        Action<string>? errorDataReceived,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        var outputCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errorCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (outputDataReceived != null)
        {
            SetupOutputDataReceived(process, outputDataReceived, outputCompletion);
        }

        if (errorDataReceived != null)
        {
            SetupErrorDataReceived(process, errorDataReceived, errorCompletion);
        }

        if (process.StartInfo.RedirectStandardOutput)
        {
            process.BeginOutputReadLine();
        }

        if (process.StartInfo.RedirectStandardError)
        {
            process.BeginErrorReadLine();
        }

        // null и TimeSpan.Zero означают «без таймаута». Условие обязано быть именно t > 0:
        // проверка t == Zero создавала CTS только для нулевого таймаута, то есть любой
        // осмысленный timeout.Value молча игнорировался и процесс висел до естественного выхода.
        using var timeoutCts = timeout is { } t && t > TimeSpan.Zero
            ? new CancellationTokenSource(t)
            : null;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts?.Token ?? CancellationToken.None);

        var waitTask = process.WaitForExitAsync(linkedCts.Token);

        // Пока читатели не дошли до EOF, а он наступает только когда процесс закрывает пайпы.
        List<Task> readerTasks = [];
        if (outputDataReceived != null)
        {
            readerTasks.Add(outputCompletion.Task);
        }

        if (errorDataReceived != null)
        {
            readerTasks.Add(errorCompletion.Task);
        }

        var readersDone = readerTasks.Count > 0
            ? Task.WhenAll(readerTasks)
            : Task.CompletedTask;

        // КогдаAny, а не КогдаAll: Task.WhenAll не прерывается на отмену (только на fault),
        // а читатели завершаются лишь тогда, когда процесс закроет пайпы. Зависший процесс —
        // ровно то, что мы ловим таймаутом — их не закроет никогда, поэтому отмена таймаута или
        // токена вызывающего уходила в ожидание естественной смерти процесса.
        var completed = await Task.WhenAny(waitTask, readersDone).ConfigureAwait(false);
        if (completed != waitTask)
        {
            // Читатели закончились первыми (пайпы закрыты, но процесс ещё жив) — ждём выхода.
            await waitTask.ConfigureAwait(false);
            return;
        }

        await waitTask.ConfigureAwait(false); // пробрасывает сбой, в т.ч. OCE при отмене

        if (!linkedCts.IsCancellationRequested)
        {
            // Нормальный выход: даём читателям донести последние буферизованные строки.
            // Без этого процесс, у которого stderr прочитан не до конца, не завершится:
            // WaitForExit ждёт опустошения буферов.
            await readersDone.ConfigureAwait(false);
        }

        // Освобождаем асинхронные reader'ы до возврата, иначе процесс держится за коллекторы
        // событий ещё какое-то время после нашего Dispose().
        if (outputDataReceived != null)
        {
            process.CancelOutputRead();
        }

        if (errorDataReceived != null)
        {
            process.CancelErrorRead();
        }
    }

    private static void SetupErrorDataReceived(
        Process process,
        Action<string> errorDataReceived,
        TaskCompletionSource<bool> errorCompletion)
    {
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) errorDataReceived(e.Data);
            else errorCompletion.TrySetResult(true);
        };
    }

    private static void SetupOutputDataReceived(
        Process process,
        Action<string> outputDataReceived,
        TaskCompletionSource<bool> outputCompletion)
    {
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) outputDataReceived(e.Data);
            else outputCompletion.TrySetResult(true);
        };
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
            using var timeoutCts = timeout is { } t && t > TimeSpan.Zero
                ? new CancellationTokenSource(t)
                : null;

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCts?.Token ?? CancellationToken.None);


            List<Task> backgroundTasks = [
                WriteToStdInAsync(process, inputStream, linkedCts.Token), // write stdin
                ReadStandardErrorToBuilderAsync(process, stderrBuilder, linkedCts.Token), // read stderr
            ];

            Stream stdoutStream = process.StandardOutput.BaseStream;
            try
            {
                await onOutput(stdoutStream, linkedCts.Token).ConfigureAwait(false);
            }
            finally
            {
                await stdoutStream.DisposeAsync().ConfigureAwait(false);
            }
            await Task.WhenAll(backgroundTasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Отмена вызывающим кодом — раньше и здесь она маскировалась в OCE от своего таймаута.
            throw;
        }
        catch (OperationCanceledException oce)
        {
            throw new ProcessTimeoutException(
                $"Process execution timed out after {Describe(timeout)}: {Describe(startInfo)}", oce);
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
            await using var _ = inputStream;
            await inputStream.CopyToAsync(process.StandardInput.BaseStream, cancellationToken)
                                 .ConfigureAwait(false);

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
public sealed class ProcessExecutionException(int exitCode, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public int Exitcode => exitCode;
}

public sealed class ProcessExecutionResultException(ProcessExecutionResult result)
    : Exception($"Process failed (exit={result.ExitCode}): {result.StandardError}")
{
    public ProcessExecutionResult Result { get; } = result;
}

public sealed class ProcessStartException(string message) : IOException(message)
{
}

public sealed class ProcessTimeoutException(string message, Exception? inner = null) : TimeoutException(message, inner)
{
}
