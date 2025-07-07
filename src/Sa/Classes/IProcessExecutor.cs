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
        );

        var result = new ProcessExecutionResult(
            exitcode,
            StandardOutput: output.ToString(),
            StandardError: error.ToString());

        if (result.ExitCode == 0 || captureErrorOutput) return result;

        throw new ProcessExecutionResultException(result);
    }

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


internal class ProcessExecutor : IProcessExecutor
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

        var outputCompletion = new TaskCompletionSource<bool>();
        var errorCompletion = new TaskCompletionSource<bool>();

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

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!process.Start())
            {
                throw new ProcessStartException($"Failed to start process: {startInfo.FileName}");
            }

            if (startInfo.RedirectStandardOutput)
                process.BeginOutputReadLine();

            if (startInfo.RedirectStandardError)
                process.BeginErrorReadLine();

            using var timeoutCts = new CancellationTokenSource(timeout ?? DefaultExecutionTimeout);

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var processTask = process.WaitForExitAsync(linkedCts.Token);

            // Wait for all streams to finish reading
            List<Task> outputTasks = [];

            if (outputDataReceived != null)
                outputTasks.Add(outputCompletion.Task);

            if (errorDataReceived != null)
                outputTasks.Add(errorCompletion.Task);

            await Task.WhenAll(processTask, Task.WhenAll(outputTasks))
                .ConfigureAwait(false);

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
            throw new ProcessExecutionException($"Process execution failed", ex);
        }
    }

    private static void TerminateProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Process termination failed: {ex.Message}");
        }
    }
}


// Custom exceptions
public class ProcessExecutionException(string message, Exception inner) : Exception(message, inner)
{
}

public class ProcessExecutionResultException(ProcessExecutionResult result)
    : Exception($"Process execution failed with exit code {result.ExitCode}. Error output: {result.StandardError}")
{
    public ProcessExecutionResult Result { get; } = result;
}

public class ProcessStartException(string message) : IOException(message)
{
}

public class ProcessTimeoutException(string message) : TimeoutException(message)
{
}
