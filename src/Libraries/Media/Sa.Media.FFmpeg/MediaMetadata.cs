namespace Sa.Media.FFmpeg;

/// <summary>
/// Metadata reported by FFprobe for a media file.
/// </summary>
/// <param name="Duration">Duration of the media in seconds, when known.</param>
/// <param name="FormatName">Name of the container format reported by FFprobe, when known.</param>
/// <param name="BitRate">Bit rate in bits per second, when known.</param>
/// <param name="Size">
/// Размер файла в байтах. Именно <see cref="long"/>: <see cref="int"/> переполнялся на файлах
/// больше 2 ГБ, и ffprobe в этом случае отдавал мусор вместо размера.
/// </param>
public sealed record MediaMetadata(
    double? Duration = null,
    string? FormatName = null,
    int? BitRate = null,
    long? Size = null
)
{
    /// <summary>
    /// An empty metadata instance with no values set.
    /// </summary>
    public static readonly MediaMetadata Empty = new();
}
