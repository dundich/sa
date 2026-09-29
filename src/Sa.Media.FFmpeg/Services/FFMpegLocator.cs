namespace Sa.Media.FFmpeg.Services;

using System.Runtime.InteropServices;

internal sealed class FFMpegLocator : IFFMpegLocator
{
    const string PlatformFolder = "sa/native";

    /// <summary>Каталог бандла относительно выходной папки приложения.</summary>
    static string BundledDirectory => Path.Combine(AppContext.BaseDirectory, "sa", "native");

    // Поиск диска не нужен: он идёт по фиксированным путям, а результат кэшируется, потому что
    // и статические IFFMpegExecutor.Default, и DI-синглтоны зовут метод многократно.
    readonly Lazy<string> _ffmpegPath;

    public FFMpegLocator() =>
        _ffmpegPath = new Lazy<string>(FindFFmpeg, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Находит путь к ffmpeg-исполняемому файлу.
    /// </summary>
    public string FindFFmpegExecutablePath()
    {
        var filePath = _ffmpegPath.Value;

        // Права трогаем ТОЛЬКО у собственного бандла. Раньше здесь вызывался chmod безусловно,
        // и на машине с системным ffmpeg (/usr/bin/ffmpeg) попытка выставить ему бит
        // завершалась TypeInitializationException — до того, как дело доходило до конвертации.
        if (IsBundled(filePath))
        {
            EnsureExecutable(filePath);
            EnsureExecutableIfExists(Sibling(filePath, Constants.FFprobeExecutableFileName));
        }

        return filePath;
    }

    /// <inheritdoc />
    public string FindFFprobeExecutablePath(string ffmpegExecutablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegExecutablePath);

        var ffprobePath = Sibling(ffmpegExecutablePath, Constants.FFprobeExecutableFileName);

        // Не требуем ffprobe при поиске ffmpeg: ffmpeg без ffprobe — валидная конфигурация,
        // а обратное — нет. Раньше тут бросался FileNotFoundException на каждом запуске.
        if (IsBundled(ffmpegExecutablePath))
            EnsureExecutableIfExists(ffprobePath);

        if (!File.Exists(ffprobePath))
            throw new FileNotFoundException(
                $"ffprobe was not found next to '{ffmpegExecutablePath}'. " +
                "FFmpeg metadata/streaming APIs need it: install the full FFmpeg build " +
                "(not a minimal one without ffprobe) or set FFMpegOptions.ExecutablePath to a " +
                "directory containing both ffmpeg and ffprobe.",
                ffprobePath);

        return ffprobePath;
    }

    static string FindFFmpeg()
    {
        var executableName = Constants.FFmpegExecutableFileName;
        var appDir = AppContext.BaseDirectory;

        // 1. текущий каталог приложения
        var fullPath = Path.Combine(appDir, executableName);
        if (File.Exists(fullPath))
            return fullPath;

        // 2. бандл, распакованный билдом (sa/native/ffmpeg)
        fullPath = Path.Combine(appDir, PlatformFolder, executableName);
        if (File.Exists(fullPath))
            return fullPath;

        // 3. системный ffmpeg из PATH
        foreach (var candidate in GetCommonSearchPaths())
        {
            try
            {
                var probe = Path.Combine(candidate, executableName);
                if (File.Exists(probe))
                    return probe;
            }
            catch
            {
                // Нечитаемый элемент PATH — не повод прерывать поиск.
            }
        }

        throw new FileNotFoundException(
            $"ffmpeg not found. Searched: '{Path.Combine(appDir, executableName)}', " +
            $"'{Path.Combine(appDir, PlatformFolder, executableName)}' and PATH. " +
            "Install FFmpeg system-wide, or set FFMpegOptions:ExecutablePath to the ffmpeg binary. " +
            "If you installed this NuGet package, the bundled binaries are extracted by the build " +
            "into 'sa/native' — check the build log for [Sa.Media.FFmpeg] warnings.",
            Constants.FFmpegExecutableFileName);
    }

    /// <summary>
    /// Файл лежит в каталоге, который распаковывает билд этого пакета. Только такие файлы
    /// безопасно модифицировать: они наши.
    /// </summary>
    internal static bool IsBundled(string path)
    {
        var bundledDir = BundledDirectory;
        var fullPath = Path.GetFullPath(path);
        return Path.GetDirectoryName(fullPath) is { } dir
               && string.Equals(dir, bundledDir, StringComparison.Ordinal);
    }

    static string Sibling(string path, string fileName) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", fileName);

    static void EnsureExecutableIfExists(string path)
    {
        if (File.Exists(path))
            EnsureExecutable(path);
    }

    /// <summary>
    /// Выставляет бит исполнения без запуска внешнего процесса: <c>chmod</c> может отсутствовать
    /// в минимальном образе, а <see cref="File.SetUnixFileMode"/> — обычный системный вызов.
    /// </summary>
    internal static void EnsureExecutable(string path)
    {
        // Именно OperatingSystem.IsWindows(), а не Constants.IsOsWindows: только встроенные
        // проверки распознаёт анализатор платформ (CA1416).
        if (OperatingSystem.IsWindows() || !File.Exists(path))
            return;

        var mode = File.GetUnixFileMode(path);
        if ((mode & UnixFileMode.UserExecute) != 0)
            return;

        try
        {
            File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new IOException(
                $"Cannot set the executable bit on the bundled FFmpeg binary '{path}'. " +
                "If it sits on a filesystem mounted with 'noexec', move the app to a normal volume " +
                "or point FFMpegOptions:ExecutablePath at your own ffmpeg.",
                e);
        }
    }

    static IEnumerable<string> GetCommonSearchPaths()
    {
        var paths = new List<string>();

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv != null)
        {
            paths.AddRange(pathEnv.Split(Path.PathSeparator));
        }

        if (Constants.IsOsWindows)
        {
            paths.AddRange(
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "ffmpeg", "bin")
            ]);
        }
        else
        {
            paths.AddRange(["/usr/local/bin", "/usr/bin", "/bin"]);
        }

        return paths.Distinct();
    }
}
