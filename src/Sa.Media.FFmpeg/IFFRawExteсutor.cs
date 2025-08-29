using Sa.Classes;
using System.Diagnostics;

namespace Sa.Media.FFmpeg;

/// <summary>
/// Interface for executing raw FFmpeg or FFprobe commands.
/// </summary>
public interface IFFRawExteсutor
{
    /// <summary>
    /// Gets the full path to the FFmpeg/FFprobe executable used by this executor.
    /// </summary>
    string ExecutablePath { get; }

    /// <summary>
    /// Gets the default timeout duration used for process execution.
    /// </summary>
    TimeSpan DefaultTimeout { get; }

    /// <summary>
    /// Executes a command with the specified arguments asynchronously.
    /// </summary>
    /// <param name="commandArguments">The command-line arguments to pass to the executable.</param>
    /// <param name="captureErrorOutput">If true, captures stderr output; otherwise ignores it.</param>
    /// <param name="timeout">Optional timeout for the execution. If not provided, uses <see cref="DefaultTimeout"/>.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the execution.</param>
    /// <returns>A task that represents the asynchronous execution result.</returns>
    Task<ProcessExecutionResult> ExecuteAsync(
        string commandArguments,
        bool captureErrorOutput = false,
        TimeSpan? timeout = null,
        Action<ProcessStartInfo>? configure = null,
        CancellationToken cancellationToken = default);


    /// <summary>
    /// Executes a command with the specified sequence of arguments asynchronously.
    /// Joins the arguments into a single string using space as a separator.
    /// </summary>
    /// <param name="commandArguments">An enumerable collection of command-line arguments.</param>
    /// <param name="captureErrorOutput">If true, captures stderr output; otherwise ignores it.</param>
    /// <param name="timeout">Optional timeout for the execution. If not provided, uses <see cref="DefaultTimeout"/>.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the execution.</param>
    /// <returns>A task that represents the asynchronous execution result.</returns>
    Task<ProcessExecutionResult> ExecuteAsync(
        IEnumerable<string> commandArguments,
        bool captureErrorOutput = false,
        TimeSpan? timeout = null,
        Action<ProcessStartInfo>? configure = null,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(string.Join(" ", commandArguments), captureErrorOutput, timeout, configure, cancellationToken);


    /// <summary>
    /// Executes stdout as a stream.
    /// Stderr is captured and checked on completion
    /// </summary>
    Task ExecuteStdOutAsync(
        string commandArguments, 
        Stream inputStream, 
        Func<Stream, Task> onOutput, 
        Action<ProcessStartInfo>? configure = null, 
        CancellationToken cancellationToken = default);
}
