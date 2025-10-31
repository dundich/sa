namespace Sa.Media.FFmpeg;

public sealed record FFMpegOptions(
    string? ExecutablePath = null,
    string? WritableDirectory = null,
    TimeSpan? Timeout = null);
