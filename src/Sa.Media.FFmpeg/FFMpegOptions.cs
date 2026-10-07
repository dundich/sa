using System.ComponentModel.DataAnnotations;

namespace Sa.Media.FFmpeg;

/// <summary>
/// Настройки библиотеки. Биндиндятся из конфигурации через <c>AddSaFFMpeg(b => b.FromConfiguration("Section"))</c>
/// и валидируются при старте хоста.
/// </summary>
/// <remarks>
/// Единственный тип конфигурации, обслуживаемый стандартным конвейером
/// <c>Microsoft.Extensions.Options</c>: <c>Configure</c> → <c>PostConfigure</c> (нормализация)
/// → валидация. Фабрика исполнителей принимает ровно этот тип, отдельной «настроечной» копии нет.
/// </remarks>
public sealed record FFMpegOptions
{
    /// <summary>
    /// Полный путь к исполняемому файлу ffmpeg. Если <c>null</c>, используется бандл
    /// <c>sa/native</c> рядом с приложением, затем поиск в PATH.
    /// Рядом с ним должен лежать ffprobe — иначе операции чтения метаданных упадут с
    /// FileNotFoundException на первой попытке.
    /// </summary>
    public string? ExecutablePath { get; set; } = null;

    /// <summary>
    /// Каталог для временных и выходных файлов по умолчанию. Нужен там, где текущий рабочий
    /// каталог процесса недоступен на запись — в Windows-службах, в контейнерах с read-only rootfs,
    /// в системах, где CWD не задан. Создаётся, если отсутствует.
    /// <c>null</c> — использовать каталог по умолчанию вызывающего кода.
    /// </summary>
    public string? WritableDirectory { get; set; } = null;

    /// <summary>
    /// Таймаут выполнения команд, секунд. <c>null</c> или <c>0</c> — использовать
    /// <see cref="Services.Constants.DefaultTimeout"/> (5 минут).
    /// </summary>
    public int? TimeoutSeconds { get; set; }

    /// <summary>
    /// Вычисленный таймаут на основе <see cref="TimeoutSeconds"/>, либо <c>null</c> — «по умолчанию».
    /// </summary>
    public TimeSpan? Timeout => TimeoutSeconds is > 0 ? TimeSpan.FromSeconds(TimeoutSeconds.Value) : null;

    /// <summary>
    /// Создаёт <see cref="WritableDirectory"/>, если он задан и ещё не существует.
    /// </summary>
    public void EnsureWritableDirectory()
    {
        if (!string.IsNullOrWhiteSpace(WritableDirectory))
            Directory.CreateDirectory(WritableDirectory);
    }

    /// <summary>
    /// Проверяет настройки и выбрасывает <see cref="ValidationException"/> с описанием первой
    /// некорректной опции.
    /// </summary>
    /// <exception cref="ValidationException">Выбрасывается, если настройки некорректны.</exception>
    /// <remarks>
    /// Вызывается <see cref="FFMpegOptionsValidator"/> уже после нормализации, поэтому проверяются
    /// именно итоговые значения. Явные проверки вместо <c>ValidateDataAnnotations()</c>: тот помечен
    /// <c>RequiresUnreferencedCode</c> (IL2026) и ломает Native AOT.
    /// </remarks>
    public void Validate()
    {
        if (TimeoutSeconds is < 0)
        {
            throw new ValidationException("FFMpegOptions:TimeoutSeconds must be non-negative or left unset.");
        }

        // Каталог создаёт фабрика (EnsureWritableDirectory), так что его отсутствие — не ошибка
        // настройки; настоящая ошибка — путь, указывающий на *файл*, в этом случае CreateDirectory
        // роняет непонятное IOException при первом resolve.
        if (WritableDirectory is not null && File.Exists(WritableDirectory))
        {
            throw new ValidationException(
                "FFMpegOptions:WritableDirectory points to a file, not a directory. " +
                "Leave the option unset to use the default, or point it at a directory (it is created if missing).");
        }
    }
}
