namespace Sa.Media.FFmpeg;

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
    public static readonly MediaMetadata Empty = new();
}
