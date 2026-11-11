namespace Sa.Media.FFmpeg.Services;

/// <summary>
/// Represents the result of a process execution, including exit code and output streams.
/// </summary>
public record ProcessExecutionResult
{
    /// <summary>The exit code returned by the executed process.</summary>
    public int ExitCode { get; init; }

    /// <summary>The standard output (stdout) captured from the process.</summary>
    public string StandardOutput { get; init; } = string.Empty;

    /// <summary>The standard error (stderr) captured from the process.</summary>
    public string StandardError { get; init; } = string.Empty;
}
