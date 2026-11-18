namespace Sa.Media.FFmpeg;

/// <summary>
/// Metadata reported by FFprobe for a media file.
/// </summary>
public sealed record MediaMetadata
{
    /// <summary>Duration of the media in seconds, when known.</summary>
    public double? Duration { get; init; }

    /// <summary>Name of the container format reported by FFprobe, when known.</summary>
    public string? FormatName { get; init; }

    /// <summary>Bit rate in bits per second, when known.</summary>
    public int? BitRate { get; init; }

    /// <summary>File size in bytes; <see cref="long"/> because int overflowed above 2 GiB.</summary>
    public long? Size { get; init; }

    /// <summary>
    /// An empty metadata instance with no values set.
    /// </summary>
    public static readonly MediaMetadata Empty = new();
}
