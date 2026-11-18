namespace Sa.Media.FFmpeg.Services;

/// <summary>
/// Процесс завершился с ненулевым кодом возврата.
/// </summary>
public sealed class ProcessExecutionException(int exitCode, string message, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>
    /// The non-zero exit code returned by the process.
    /// </summary>
    public int ExitCode { get; } = exitCode;
}

/// <summary>
/// Процесс завершился с ненулевым кодом возврата, и вызывающая сторона попросила бросить
/// исключение (<c>throwOnError: true</c>). Несёт stderr — он и объясняет, что пошло не так.
/// </summary>
public sealed class ProcessExecutionResultException(ProcessExecutionResult result, string? commandLine = null)
    : Exception($"Process failed (exit={result.ExitCode})"
                + (string.IsNullOrEmpty(commandLine) ? "" : $": {commandLine}")
                + $"{Environment.NewLine}{result.StandardError}")
{
    /// <summary>Результат выполнения процесса, из которого извлечён код возврата и stderr.</summary>
    public ProcessExecutionResult Result { get; } = result;

    /// <summary>Команда, которая завершилась с ошибкой. Для диагностики и логирования.</summary>
    public string? CommandLine { get; } = commandLine;
}

/// <summary>Не удалось запустить процесс (например, исполняемого файла нет).</summary>
public sealed class ProcessStartException(string message) : IOException(message)
{
}

/// <summary>
/// Процесс не уложился в <c>timeout</c>. Никогда не бросается для отмены вызывающей стороной:
/// для неё тип исключения — <see cref="OperationCanceledException"/>. Различать их важно,
/// потому что отмена пользователя и истечение таймаута требуют разной реакции.
/// </summary>
public sealed class ProcessTimeoutException(string message, Exception? inner = null)
    : TimeoutException(message, inner)
{
}
