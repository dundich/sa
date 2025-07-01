using Sa.Classes;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace Sa.Media.FFmpeg;

internal sealed class FFMpegExecutor(IProcessExecutor processExecutor, string executablePath) : IFFmpegExecutor
{

    static class Constants
    {
        public const string FFmpegFileNameWin = "ffmpeg.exe";
        public const string FFmpegFileNameLinux = "ffmpeg";
        public const string FFprobeFileNameWin = "ffprobe.exe";
        public const string FFprobeFileNameLinux = "ffprobe";
        public static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5 * 60);

        public static bool IsOsWindows { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        public static bool IsOsLinux { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

        public static string FFmpegExecutableFileName { get; } = IsOsWindows
            ? FFmpegFileNameWin
            : FFmpegFileNameLinux;

        public static string FFprobeExecutableFileName { get; } = IsOsWindows
            ? FFprobeFileNameWin
            : FFprobeFileNameLinux;
    }


    public static TimeSpan DefaultTimeout { get; set; } = Constants.Timeout;

    public string FFmpegExecutablePath => executablePath;

    public string FFprobeExecutablePath { get; }
        = Path.Combine(Path.GetDirectoryName(executablePath)!, Constants.FFprobeExecutableFileName);

    public Task<ProcessExecutionResult> ExecuteFFmpegAsync(
        string commandArguments,
        bool captureErrorOutput = false,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        return processExecutor.ExecuteWithResultAsync(
            GetStartInfo(FFmpegExecutablePath, commandArguments)
            , captureErrorOutput
            , timeout
            , cancellationToken);
    }

    public Task<ProcessExecutionResult> ExecuteFFprobeAsync(
        string commandArguments,
        bool captureErrorOutput = false,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        return processExecutor.ExecuteWithResultAsync(
            GetStartInfo(FFprobeExecutablePath, commandArguments)
            , captureErrorOutput
            , timeout
            , cancellationToken);
    }


    private static ProcessStartInfo GetStartInfo(string path, string arguments)
    {
        return new ProcessStartInfo
        {
            FileName = path,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
    }

    public static FFMpegExecutor Create(
        IProcessExecutor? processExecutor = null,
        string? executablePath = null,
        string? writableDirectory = null)
    {
        executablePath ??= FindFFmpegExecutablePath(writableDirectory);

        if (!File.Exists(executablePath))
            throw new FileNotFoundException("FFmpeg executable not found", executablePath);

        processExecutor ??= IProcessExecutor.Default;

        return new FFMpegExecutor(processExecutor, executablePath);
    }

    public async Task<string> GetVersion(CancellationToken cancellationToken = default)
    {
        var result = await ExecuteFFmpegAsync("-version", cancellationToken: cancellationToken);
        return result.StandardOutput;
    }

    public async Task<string> GetFormats(CancellationToken cancellationToken = default)
    {
        var result = await ExecuteFFmpegAsync("-formats", cancellationToken: cancellationToken);
        return result.StandardOutput;
    }

    public async Task<string> GetCodecs(CancellationToken cancellationToken = default)
    {
        var result = await ExecuteFFmpegAsync("-codecs", cancellationToken: cancellationToken);
        return result.StandardOutput;
    }

    public async Task<string> ConvertToPcmS16Le(string inputFileName, string outputFileName, int? targetSampleRate = null, bool isOverwrite = false, CancellationToken cancellationToken = default)
    {
        var sampleRate = targetSampleRate != null ? $"-ar {targetSampleRate}" : string.Empty;

        var result = await ExecuteFFmpegAsync($"{(isOverwrite ? "-y" : string.Empty)} -i \"{inputFileName}\" -acodec pcm_s16le {sampleRate} -f wav \"{outputFileName}\"", cancellationToken: cancellationToken);
        return result.StandardError;
    }

    public async Task<string> ConvertToMp3(string inputFileName, string outputFileName, bool isOverwrite = false, CancellationToken cancellationToken = default)
    {
        var isOver = isOverwrite ? "-y" : string.Empty;
        var cmd = Constants.IsOsLinux
            ? $"{isOver} - i \"{inputFileName}\" -f mp3 -c:a libmp3lame \"{outputFileName}\""
            : $"{isOver} -i \"{inputFileName}\" -f mp3 \"{outputFileName}\"";

        // ffmpeg - i input.wav -c:a libmp3lame output.mp3
        var result = await ExecuteFFmpegAsync(cmd, cancellationToken: cancellationToken);
        return result.StandardError;
    }

    public async Task<string> ConvertToOgg(string inputFileName, string outputFileName, bool isLibopus = false, bool isOverwrite = false, CancellationToken cancellationToken = default)
    {
        var isOver = isOverwrite ? "-y" : string.Empty;
        var cmd = Constants.IsOsLinux
            ? $"{isOver} - i \"{inputFileName}\" -f ogg -c:a {GetCodec(isLibopus)} \"{outputFileName}\""
            : $"{isOver} -i \"{inputFileName}\" -f ogg \"{outputFileName}\"";

        var result = await ExecuteFFmpegAsync(cmd, cancellationToken: cancellationToken);
        return result.StandardError;

        static string GetCodec(bool isLibopus)
        {
            return (isLibopus ? "libopus" : "libvorbis");
        }
    }

    public async Task<int> GetAudioChannelCount(string filePath, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteFFprobeAsync($"-v error -select_streams a:0 -show_entries stream=channels -of csv=p=0 \"{filePath}\"",
            cancellationToken: cancellationToken);

        if (int.TryParse(result.StandardOutput.Trim(), out int channels))
        {
            return channels;
        }

        throw new InvalidDataException("Failed to determine the number of audio channels.");
    }

    public static FFMpegExecutor Default => s_default.Value;


    private static readonly Lazy<FFMpegExecutor> s_default = new(() => Create());

    private static string FindFFmpegExecutablePath(string? writableDirectory)
    {
        var appDir = AppContext.BaseDirectory;

        var fullPath = Path.Combine(appDir, ExecutableName);

        // 0. Проверяем, существует ли файл в текущей директории
        if (File.Exists(fullPath))
        {
            return fullPath;
        }

        // 1. Проверяем платформозависимый путь
        string platformPath = GetPlatformFolderPath(ExecutableName);

        fullPath = Path.Combine(appDir, platformPath);
        if (File.Exists(fullPath))
        {
            return fullPath;
        }

        // 2. Проверяем стандартные пути установки
        foreach (var dir in GetCommonSearchPaths())
        {
            try
            {
                var candidate = Path.Combine(dir, ExecutableName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Ignore access issues
            }
        }

        // 3. Извлечение из ресурсов
        return ExtractFFmpegFromResources(platformPath, writableDirectory ?? FindWritableDirectory());
    }


    private static string GetPlatformFolderPath(string executableName)
    {
        string platformPath;
        if (Constants.IsOsWindows)
        {
            platformPath = Path.Combine("runtimes", "win-x64", executableName);
        }
        else if (Constants.IsOsLinux)
        {
            platformPath = Path.Combine("runtimes", "linux-x64", executableName);
        }
        else
        {
            throw new PlatformNotSupportedException(
                $"Platform '{RuntimeInformation.OSDescription}' is not supported.");
        }

        return platformPath;
    }


    private static string ExecutableName => Constants.FFmpegExecutableFileName;

    private static IEnumerable<string> GetCommonSearchPaths()
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

    private static string ExtractFFmpegFromResources(string relativePath, string storagePath)
    {
        var ffmpegBinaryPath = Path.Combine(storagePath, ExecutableName);

        if (File.Exists(ffmpegBinaryPath))
        {
            return ffmpegBinaryPath;
        }

        var destDir = Path.GetDirectoryName(Path.GetFullPath(ffmpegBinaryPath))!;
        Directory.CreateDirectory(destDir);

        var respath = Path.ChangeExtension(relativePath, "zip")
            .Replace('\\', '.')
            .Replace('/', '.')
            .Replace('-', '_');

        var assembly = typeof(FFMpegExecutor).Assembly;

        var resourceName = $"{assembly.GetName().Name}.{respath}";

        using var zipStream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");

        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

        foreach (var entry in archive.Entries)
        {
            entry.ExtractToFile(Path.Combine(destDir, entry.Name), overwrite: true);
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            MakeFileExecutable(Path.Combine(destDir, Constants.FFmpegFileNameLinux));
            MakeFileExecutable(Path.Combine(destDir, Constants.FFprobeFileNameLinux));
        }

        return ffmpegBinaryPath;
    }

    public static string FindWritableDirectory()
    {
        string[] candidates =
        [
            Path.Combine(Directory.GetCurrentDirectory(), "data"),
            Path.GetTempPath()
        ];

        return Array.Find(candidates, CanWritableDirectory)
            ?? throw new IOException("No writable directory found for storing temporary files.");
    }

    private static bool CanWritableDirectory(string dir)
    {
        try
        {
            // Создаём каталог, если его нет
            Directory.CreateDirectory(dir);

            var testFile = Path.Combine(dir, ".write_test");
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
            return true;
        }
        catch (Exception ex)
        {
            // Не можем писать — пропускаем
            // Логируем ошибку
            Debug.WriteLine($"Write test failed for {dir}: {ex.Message}");
        }

        return false;
    }

    private static void MakeFileExecutable(string path)
    {
        using var process = Process.Start(GetStartInfo("chmod", $"u+x \"{path}\""));
        process?.WaitForExit();

        if (process?.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to make {path} executable.");
        }
    }
}
