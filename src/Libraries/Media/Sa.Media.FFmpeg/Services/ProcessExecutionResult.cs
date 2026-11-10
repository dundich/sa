namespace Sa.Media.FFmpeg.Services;

/// <summary>
/// Represents the result of a process execution, including exit code and output streams.
/// </summary>
/// <param name="ExitCode">
/// The exit code returned by the executed process.
/// A value of 0 typically indicates success.
/// </param>
/// <param name="StandardOutput">
/// The standard output (stdout) captured from the process.
/// </param>
/// <param name="StandardError">
/// The standard error (stderr) captured from the process.
/// </param>
public record ProcessExecutionResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);
