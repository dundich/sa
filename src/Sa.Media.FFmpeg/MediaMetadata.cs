namespace Sa.Media.FFmpeg;

public record MediaMetadata(
    double? Duration = null,
    string? FormatName = null,
    int? BitRate = null,
    int? Size = null
)
{
    public static readonly MediaMetadata Empty = new();
}
