using Sa.Classes;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace Sa.Media.FFmpeg;

internal sealed class FFMpegExecutor(IProcessExecutor processExecutor, string executablePath) : IFFmpegExecutor
{
    public static TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(5 * 60);

    public string ExecutablePath { get; } = executablePath;

    public Task<ProcessExecutionResult> ExecuteAsync(
        string commandArguments,
        bool captureErrorOutput = true,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        return processExecutor.ExecuteWithResultAsync(
            GetStartInfo(ExecutablePath, commandArguments)
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
        var result = await processExecutor.ExecuteWithResultAsync(
            GetStartInfo(ExecutablePath, "-version"),
            captureErrorOutput: false,
            cancellationToken: cancellationToken);

        return result.StandardOutput;
    }

    public static FFMpegExecutor Default => s_default.Value;


    private static readonly Lazy<FFMpegExecutor> s_default = new(() => Create());

    private static string FindFFmpegExecutablePath(string? writableDirectory)
    {
        var appDir = AppContext.BaseDirectory;

        string executableName = GetExecutableName();

        var fullPath = Path.Combine(appDir, executableName);

        // 0. Проверяем, существует ли файл в текущей директории
        if (File.Exists(fullPath))
        {
            return fullPath;
        }

        // 1. Проверяем платформозависимый путь
        string platformPath = GetPlatformFolderPath(executableName);

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
                var candidate = Path.Combine(dir, executableName);
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
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            platformPath = Path.Combine("runtimes", "win-x64", executableName);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
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

    private static string GetExecutableName()
    {
        // Проверяем стандартные пути установки
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "ffmpeg.exe"
            : "ffmpeg";
    }

    private static IEnumerable<string> GetCommonSearchPaths()
    {
        var paths = new List<string>();

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv != null)
        {
            paths.AddRange(pathEnv.Split(Path.PathSeparator));
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
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
        var executableName = GetExecutableName();

        var ffmpegBinaryPath = Path.Combine(storagePath, executableName);

        // Удаляем старый файл, если он был
        if (File.Exists(ffmpegBinaryPath))
        {
            return ffmpegBinaryPath;
        }

        var respath = Path.ChangeExtension(relativePath, "zip")
            .Replace('\\', '.')
            .Replace('/', '.')
            .Replace('-', '_');

        var assembly = typeof(FFMpegExecutor).Assembly;

        var resourceName = $"{assembly.GetName().Name}.{respath}";

        using var zipStream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");

        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);


        // Создаем директорию, если не существует
        Directory.CreateDirectory(storagePath);

        // Извлекаем все файлы
        foreach (var entry in archive.Entries)
        {
            var destinationPath = Path.GetFullPath(ffmpegBinaryPath);

            // Создаем поддиректории, если нужно
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

            // Извлекаем файл
            entry.ExtractToFile(destinationPath, overwrite: true);
        }

        // Делаем исполняемым на Linux/macOS
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            MakeFileExecutable(ffmpegBinaryPath);
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
