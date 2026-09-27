using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Sa.Media.FFmpeg.Services;



internal sealed class ProcessExecutor(ILogger<ProcessExecutor>? logger = null) : IProcessExecutor
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
            // process.Dispose() делает using-область; второй вызов ничего не добавит.
            throw new ProcessStartException(
                $"Failed to start process: '{startInfo.FileName}' with arguments '{startInfo.Arguments}'");
        }


        int exitCode = await ExecuteProcessWithHandlersAsync(
            process, outputDataReceived, errorDataReceived, timeout, cancellationToken)
            .ConfigureAwait(false);

        return exitCode;
    }

    private async Task<int> ExecuteProcessWithHandlersAsync(
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
            // Ни CancellationToken вызывающего, ни... — отменился только наш таймаут.
            throw new ProcessTimeoutException(
                $"Process execution timed out after {Describe(timeout)}: " +
                $"{Describe(process.StartInfo)}", oce);
        }
        catch (Exception ex)
        {
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

    internal static string Describe(ProcessStartInfo startInfo) =>
        string.IsNullOrEmpty(startInfo.Arguments) ? startInfo.FileName : $"{startInfo.FileName} {startInfo.Arguments}";

    static string Describe(TimeSpan? timeout) => timeout is { } t ? t.ToString() : "(none)";

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

        // null и TimeSpan.Zero означают «без таймаута» — одинаково во всех точках входа.
        using var timeoutCts = timeout is { } t && t > TimeSpan.Zero
            ? new CancellationTokenSource(t)
            : null;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts?.Token ?? CancellationToken.None);

        var waitTask = process.WaitForExitAsync(linkedCts.Token);

        // Wait for all streams to finish reading
        var readerTasks = new List<Task>();
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

        // Собираем оба условия через WhenAny, а не WhenAll: читатели завершаются только тогда,
        // когда процесс закрывает пайпы, а зависший процесс (то, что мы и ловим таймаутом)
        // не закроет их никогда. WhenAll после отмены таймаута ждал бы до естественной смерти
        // процесса — проверено: 2-секундный таймаут срывался через 30 секунд.
        var completed = await Task.WhenAny(waitTask, readersDone).ConfigureAwait(false);
        if (completed != waitTask)
        {
            // Читатели закончились первыми (процесс закрыл пайпы, но ещё жив): ждём выхода —
            // он либо завершится, либо отменится таймаутом.
            await waitTask.ConfigureAwait(false);
            return;
        }

        await waitTask.ConfigureAwait(false); // пробрасывает сбой, в т.ч. OCE при отмене

        if (!linkedCts.IsCancellationRequested)
        {
            // Нормальный выход: даём читателям донести последние буферизованные строки.
            // Без этого процесс, у которого stderr не до конца прочитан (а FFmpeg на битом
            // потоке пишет туда постоянно), не завершится: WaitForExit ждёт опустошения буферов.
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
            throw new ProcessStartException(
                $"Failed to start process: '{startInfo.FileName}' with arguments '{startInfo.Arguments}'");
        }

        var stderrBuilder = new BoundedStringBuilder(IProcessExecutor.StandardErrorTailLimit);
        int exitCode;
        try
        {
            // null и TimeSpan.Zero означают «без таймаута» — как и в ExecuteAsync.
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
                try
                {
                    await stdoutStream.DisposeAsync().ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // FFprobe may have closed stdout before we finish reading — benign
                }
            }
            await Task.WhenAll(backgroundTasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException oce)
        {
            // stderr, накопленный до дедлайна, полезнее пустого сообщения: зависший процесс
            // обычно застрял на ошибке, которую видно именно там.
            throw new ProcessTimeoutException(
                $"Process execution timed out after {Describe(timeout)}: {Describe(startInfo)}" +
                (stderrBuilder.Length > 0 ? $"{Environment.NewLine}{stderrBuilder}" : ""),
                oce);
        }
        finally
        {
            exitCode = SafeDisposeProcess(process);
        }

        if (exitCode != 0)
        {
            throw new ProcessExecutionException(
                exitCode,
                $"Process failed (exit={exitCode}): {Describe(startInfo)}{Environment.NewLine}{stderrBuilder}");
        }
    }


    private static async Task ReadStandardErrorToBuilderAsync(
        Process process,
        BoundedStringBuilder errorBuilder,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var buffer = new char[4096];
            while (true)
            {
                var read = await process.StandardError.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                // Строки короткие, аллокация ограничена; усечение делает BoundedStringBuilder.
                foreach (var chunk in new string(buffer, 0, read).Split('\n'))
                    if (chunk.Length > 0)
                        errorBuilder.AppendLine(chunk.TrimEnd('\r'));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (errorBuilder.Length == 0)
                errorBuilder.AppendLine("stderr: read interrupted (cancellation).");
        }
        catch (Exception ex)
        {
            errorBuilder.AppendLine($"stderr: read failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Асинхронно записывает входной поток в stdin процесса.
    /// Закрывает stdin после завершения.
    /// ВАЖНО: поток вызывающего НЕ закрывается и НЕ освобождается — он остаётся
    /// ответственностью вызывающего. Раньше здесь стоял 'await using', из-за чего после
    /// ConvertToPcmS16Le(stream, ...) повторное использование того же FileStream падало с
    /// ObjectDisposedException.
    /// </summary>
    private static async Task WriteToStdInAsync(
        Process process,
        Stream inputStream,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // input to stdin
            await inputStream.CopyToAsync(process.StandardInput.BaseStream, cancellationToken)
                             .ConfigureAwait(false);

            // Завершаем запись — FFprobe may have already closed stdin pipe
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch (OperationCanceledException)
        {
            // Cancellation — close stdin to unblock FFmpeg
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // Pipe closed by reader (FFprobe) or copy failed — benign
            try { process.StandardInput.Close(); } catch { /* skip */ }
        }
        catch (ObjectDisposedException)
        {
            // StandardInput may be disposed if process has exited
        }
    }

    private int SafeDisposeProcess(Process process)
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
            if (process.WaitForExit(Constants.ShutdownGracePeriodMs))
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

            if (process.WaitForExit(Constants.ShutdownKillTimeoutMs))
            {
                exitCode = process.ExitCode;
            }
            else
            {
                logger?.LogWarning("Process did not terminate after Kill()");
            }
        }
        catch (InvalidOperationException ex)
        {
            logger?.LogWarning(ex, "Process is invalid or already disposed.");
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Unexpected error during process termination");
        }
        finally
        {
            try
            {
                process.Dispose();
            }
            catch
            {
                // skip
            }
        }

        return exitCode;
    }
}
