using Sa.Media.FFmpeg;
using Sa.Media.FFmpeg.Services;

namespace Sa.Media.FFmpegTests;

/// <summary>
/// <see cref="FFMpegLocator"/> — поиск ffmpeg/ffprobe и chmod только собственных файлов.
/// <para>
/// Ключевая регрессия: раньше chmod применялся к любому найденному бинарнику, и на машине
/// с системным <c>/usr/bin/ffmpeg</c> (чужой файл, чужие права) первая же попытка роняла
/// TypeInitializationException в конструкторе <c>IFFMpegExecutor.Default</c> — до того,
/// как дело доходило до конвертации.
/// </para>
/// </summary>
public sealed class FFMpegLocatorTests
{
    [Fact]
    public void IsBundled_TrueOnlyForTheBundleDirectory()
    {
        // Бандл, который распаковывает билд пакета, — «наш» каталог.
        Assert.True(FFMpegLocator.IsBundled(
            Path.Combine(AppContext.BaseDirectory, "sa", "native", Constants.FFmpegExecutableFileName)));

        // Копия бинарника прямо в выходном каталоге приложения (приоритет 1 в поиске) — не бандл.
        Assert.False(FFMpegLocator.IsBundled(
            Path.Combine(AppContext.BaseDirectory, Constants.FFmpegExecutableFileName)));

        // Системный бинарный — сценарий из отчёта о регрессии.
        Assert.False(FFMpegLocator.IsBundled("/usr/bin/ffmpeg"));
    }

    [Fact]
    public void FindFFmpegExecutablePath_ResolvesWithoutThrowing()
    {
        // Интеграционный чек: в этой среде ffmpeg находится (бандл в sa/native, копия в
        // выходном каталоге или системный) и при этом никто не пытается chmod'ить
        // чужой бинарник.
        var path = new FFMpegLocator().FindFFmpegExecutablePath();

        Assert.True(File.Exists(path));
        Assert.Equal(Constants.FFmpegExecutableFileName, Path.GetFileName(path));
    }

    [Fact]
    public void EnsureExecutable_SetsTheExecuteBitOnOurOwnFile()
    {
        // На Windows нет permission-битов — метод сознательно no-op.
        if (OperatingSystem.IsWindows())
            return;

        var path = Path.Combine(Path.GetTempPath(), $"sa-ffmpeg-test-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(path, "fake binary");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            FFMpegLocator.EnsureExecutable(path);

            Assert.Equal(
                UnixFileMode.UserExecute,
                File.GetUnixFileMode(path) & UnixFileMode.UserExecute);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void EnsureExecutable_DoesNotThrowWhenTheFileIsMissing() =>
        FFMpegLocator.EnsureExecutable(Path.Combine(Path.GetTempPath(), "definitely-not-here", "ffmpeg"));

    [Fact]
    public void FindFFprobeExecutablePath_ReturnsSiblingOrThrowsHelpfully()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sa-ffmpeg-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var ffmpeg = Path.Combine(dir, Constants.FFmpegExecutableFileName);
        var ffprobe = Path.Combine(dir, Constants.FFprobeExecutableFileName);
        File.WriteAllText(ffmpeg, "fake");

        try
        {
            // ffprobe отсутствует — внятный FileNotFoundException, а не крах при первой
            // GetMetaInfo: ffmpeg без ffprobe — валидная конфигурация, ffprobe без ffmpeg — нет.
            var ex = Assert.Throws<FileNotFoundException>(
                () => new FFMpegLocator().FindFFprobeExecutablePath(ffmpeg));
            Assert.Contains("ffprobe", ex.Message, StringComparison.OrdinalIgnoreCase);

            // Файл рядом — возвращается соседний путь.
            File.WriteAllText(ffprobe, "fake");
            Assert.Equal(ffprobe, new FFMpegLocator().FindFFprobeExecutablePath(ffmpeg));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
