using System.ComponentModel.DataAnnotations;

namespace Sa.Media.FFmpeg;

public sealed record FFMpegOptions
{
    [StringLength(255)]
    public string? ExecutablePath { get; set; } = null;

    [StringLength(255)]
    public string? WritableDirectory { get; set; } = null;

    public int? TimeoutSeconds { get; set; }

    public TimeSpan? Timeout => TimeoutSeconds > 0
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
