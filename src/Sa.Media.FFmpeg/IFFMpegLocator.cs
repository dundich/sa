namespace Sa.Media.FFmpeg;

public interface IFFMpegLocator
{
    string FindFFmpegExecutablePath(string? writableDirectory = null);
}
