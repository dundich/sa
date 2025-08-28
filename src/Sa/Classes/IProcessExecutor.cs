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
    /// Executes a process and returns stdout as a stream.
    /// Stderr is captured and checked on completion.
    /// The process is terminated if the stream is disposed before natural exit.
    /// </summary>
    Stream ExecuteStream(ProcessStartInfo startInfo, Stream inputStream);


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
        startInfo.RedirectStandardOutput = outputDataReceived != null;
        startInfo.RedirectStandardError = errorDataReceived != null;

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        var outputCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errorCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!process.Start())
            {
                throw new ProcessStartException($"Failed to start process: '{startInfo.FileName}' with arguments '{startInfo.Arguments}'");
            }

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

            return process.ExitCode;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TerminateProcess(process);
            throw new ProcessTimeoutException("Process execution timed out");
        }
        catch (Exception ex)
        {
            TerminateProcess(process);
            throw new ProcessExecutionException(process.ExitCode, "Process execution failed", ex);
        }
    }

    /// <summary>
    /// Запускает процесс и возвращает поток stdout. 
    /// Поток stderr собирается автоматически. 
    /// При завершении чтения или Dispose — проверяется результат.
    /// </summary>
    public Stream ExecuteStream(ProcessStartInfo startInfo, Stream inputStream)
    {
        // Настройка
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        try
        {
            if (!process.Start())
                throw new ProcessStartException($"Failed to start process: '{startInfo.FileName}' with arguments '{startInfo.Arguments}'");

            StringBuilder stderrBuilder = new();
            List<Task> tasks = [
                WriteToStdInAsync(process, inputStream), // write stdin
                ReadStandardErrorToBuilderAsync(process, stderrBuilder) // read stderr
            ];

            // process.BeginOutputReadLine();
            // process.BeginErrorReadLine();

            // wrap stream
            return new ProcessOutputStream(
                baseStream: process.StandardOutput.BaseStream,
                onDisposeAsync: async () =>
                {
                    try
                    {
                        // Дожидаемся завершения фоновых задач
                        await Task.WhenAll(tasks).ConfigureAwait(false);
                    }
                    catch
                    {
                        // suppress
                    }

                    try
                    {
                        var exitCode = process.ExitCode;

                        if (exitCode != 0)
                        {
                            TerminateProcess(process);
                            throw new ProcessExecutionException(exitCode, stderrBuilder.ToString());
                        }
                    }
                    finally
                    {
                        process.Dispose();
                    }
                });
        }
        catch (Exception)
        {
            process.Dispose();
            throw;
        }
    }


    // <summary>
    /// Асинхронно читает stderr процесса и добавляет в StringBuilder.
    /// </summary>
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
            // Допустимо при отмене
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
            // Копируем входной поток в stdin
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
            // Корректная отмена — не ошибка
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // Возможна ошибка при разрыве соединения или закрытии процесса
            process.StandardInput.Close();
        }
        catch (Exception ex)
        {
            process.StandardInput.Close();
            throw new IOException("Failed to write input stream to process stdin.", ex);
        }
    }

    private static void TerminateProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            process.WaitForExit(500);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Process termination failed: {ex.Message}");
        }
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


/// <summary>
/// Обёртка над stdout процесса. При Dispose завершает процесс и проверяет ошибки.
/// </summary>
internal sealed class ProcessOutputStream(
    Stream baseStream,
    Func<Task> onDisposeAsync
) : Stream
{
    private bool _disposed;

    // Проброс всех методов Stream
    public override bool CanRead => baseStream.CanRead;
    public override bool CanSeek => baseStream.CanSeek;
    public override bool CanWrite => baseStream.CanWrite;
    public override long Length => baseStream.Length;
    public override long Position
    {
        get => baseStream.Position;
        set => baseStream.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        baseStream.Read(buffer, offset, count);

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        try
        {
            return await baseStream.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Если ошибка при чтении — всё равно попробуем получить результат
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            return await baseStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Если ошибка при чтении — всё равно попробуем получить результат
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public override void Flush() => baseStream.Flush();
    public override async Task FlushAsync(CancellationToken cancellationToken) =>
        await baseStream.FlushAsync(cancellationToken).ConfigureAwait(false);

    public override long Seek(long offset, SeekOrigin origin) =>
        baseStream.Seek(offset, origin);

    public override void SetLength(long value) => baseStream.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    // Основная логика: при Dispose — завершаем процесс и проверяем ошибки
    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;

        if (disposing)
        {
            try
            {
                baseStream?.Dispose();
            }
            catch
            {
                // suppress
            }

            try
            {
                onDisposeAsync().GetAwaiter().GetResult(); // Синхронное ожидание dispose
            }
            catch (Exception e)
            {
                Debug.WriteLine(e);
                // suppress
            }
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            await baseStream.DisposeAsync();
        }
        catch
        {
            // suppress
        }

        try
        {
            await onDisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // suppress
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }
}