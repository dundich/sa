namespace Sa.Media.FFmpeg;

public record FFMpegOptions(
    string? ExecutablePath = null,
    string? WritableDirectory = null,
    TimeSpan? Timeout = null);
