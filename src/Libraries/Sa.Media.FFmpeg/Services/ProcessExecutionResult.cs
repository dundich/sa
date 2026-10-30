namespace Sa.Media.FFmpeg.Services;

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
