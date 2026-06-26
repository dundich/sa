using System.ComponentModel.DataAnnotations;

namespace Sa.Media.FFmpeg;

public sealed record FFMpegOptions
{
    /// <summary>
    /// Полный путь к исполняемому файлу ffmpeg/ffprobe. Если <c>null</c>, используется поиск через PATH или sa/native/.
    /// </summary>
    [StringLength(255)]
    public string? ExecutablePath { get; set; } = null;

    /// <summary>
    /// Директория, в которую FFmpeg может записывать выходные файлы.
    /// </summary>
    [StringLength(255)]
    public string? WritableDirectory { get; set; } = null;

    /// <summary>
    /// Таймаут выполнения команд в секундах. По умолчанию используется 5 минут (Constants.DefaultTimeout).
    /// </summary>
    public int? TimeoutSeconds { get; set; }

    /// <summary>
    /// Вычисленный таймаут на основе <see cref="TimeoutSeconds"/>.
    /// </summary>
    public TimeSpan? Timeout => TimeoutSeconds > 0
        ? TimeSpan.FromSeconds(TimeoutSeconds.Value)
        : null;

    /// <summary>
    /// Валидирует параметры после десериализации.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">Если WritableDirectory не существует.</exception>
    /// <exception cref="ArgumentException">Если TimeoutSeconds отрицательный.</exception>
    public void Validate()
    {
        if (WritableDirectory is not null && !Directory.Exists(WritableDirectory))
        {
            throw new DirectoryNotFoundException(
                $"FFmpeg writable directory does not exist: {WritableDirectory}");
        }

        if (TimeoutSeconds.HasValue && TimeoutSeconds.Value < 0)
        {
            throw new ArgumentException("TimeoutSeconds must be non-negative", nameof(TimeoutSeconds));
        }
    }
}
