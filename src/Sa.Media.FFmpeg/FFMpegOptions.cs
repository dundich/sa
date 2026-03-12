using System.ComponentModel.DataAnnotations;

namespace Sa.Media.FFmpeg;

public sealed record FFMpegOptions
{
    [StringLength(500)]
    public string? ExecutablePath { get; set; } = null;

    [StringLength(500)]
    public string? WritableDirectory { get; set; } = null;

    public int? TimeoutSeconds { get; set; }

    public TimeSpan? Timeout => TimeoutSeconds.HasValue
        ? TimeSpan.FromSeconds(TimeoutSeconds.Value)
        : default;

    // Валидация после десериализации
    public void Validate()
    {
        if (WritableDirectory is not null && !Directory.Exists(WritableDirectory))
        {
            throw new DirectoryNotFoundException(
                $"FFmpeg writable directory does not exist: {WritableDirectory}");
        }
    }
}
