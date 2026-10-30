namespace Sa.Media.FFmpeg;

public interface IFFMpegLocator
{
    /// <summary>
    /// Путь к исполняемому файлу ffmpeg: сначала бандл <c>sa/native</c> рядом с приложением,
    /// затем ffmpeg из <c>PATH</c>.
    /// </summary>
    string FindFFmpegExecutablePath();

    /// <summary>
    /// Путь к ffprobe, лежащему рядом с <paramref name="ffmpegExecutablePath"/>.
    /// Выбрасывает <see cref="FileNotFoundException"/>, если рядом с ffmpeg нет ffprobe.
    /// </summary>
    string FindFFprobeExecutablePath(string ffmpegExecutablePath);
}
