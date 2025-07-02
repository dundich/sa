namespace Sa.Media.FFmpeg.Services;

public interface IFFMpegLocator
{
    string FindFFmpegExecutablePath(string? writableDirectory = null);
}
