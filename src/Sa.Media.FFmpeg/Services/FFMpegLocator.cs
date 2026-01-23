using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Sa.Media.FFmpeg.Services;

internal sealed class FFMpegLocator : IFFMpegLocator
{
    /// <summary>
    /// Находит путь к ffmpeg-исполняемому файлу.
    /// </summary>
    public string FindFFmpegExecutablePath(string? writableDirectory = null)
    {
        var executableName = Constants.FFmpegExecutableFileName;

        var appDir = AppContext.BaseDirectory;

        // 1. current dir
        var fullPath = Path.Combine(appDir, executableName);
        if (File.Exists(fullPath))
            return fullPath;

        // 2. (runtimes/win-x64)
        string platformPath = GetPlatformFolder();
        fullPath = Path.Combine(appDir, platformPath, executableName);
        if (File.Exists(fullPath))
            return fullPath;

        // 3. in system PATH
        foreach (var dir in GetCommonSearchPaths())
        {
            try
            {
                var candidate = Path.Combine(dir, executableName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // ignore error
            }
        }

        // 4. from resx
        writableDirectory ??= FindWritableDirectory();
        return ExtractFFmpegFromResources(platformPath, writableDirectory);
    }


    static readonly Lock s_lock = new();


    /// <summary>
    /// Извлекает ffmpeg из встроенных ресурсов ассембли.
    /// </summary>
    private static string ExtractFFmpegFromResources(string relativePath, string destDir)
    {
        var resourcePath = Path.ChangeExtension(Path.Combine(relativePath, Constants.FFmpegExecutableFileName), "zip")
            .Replace('\\', '.')
            .Replace('/', '.')
            .Replace('-', '_');

        var assembly = typeof(FFMpegLocator).Assembly;
        var resourceName = $"{assembly.GetName().Name}.{resourcePath}";

        using var zipStream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");

        Directory.CreateDirectory(destDir);

        string executableFile = Path.Combine(destDir, Constants.FFmpegExecutableFileName);

        lock (s_lock)
        {
            if (File.Exists(executableFile)) return executableFile;

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
        }

        return executableFile;
    }

    /// <summary>
    /// Делает файл исполняемым (только для Linux/macOS).
    /// </summary>
    private static void MakeFileExecutable(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "chmod",
            Arguments = $"u+x \"{path}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        });

        process?.WaitForExit();

        if (process?.ExitCode != 0)
            throw new InvalidOperationException($"Failed to make file executable: {path}");
    }

    /// <summary>
    /// Find a Suitable Directory for Temporary Files
    /// </summary>
    private static string FindWritableDirectory()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, GetPlatformFolder())
            , Path.GetTempPath()
        ];

        return Array.Find(candidates, CanWriteToDirectory)
            ?? throw new IOException("No writable directory found.");
    }

    private static string GetPlatformFolder()
    {
        return Constants.IsOsLinux
            ? Path.Combine("runtimes", "linux-x64")
            : Path.Combine("runtimes", "win-x64");
    }

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

    private static bool CanWriteToDirectory(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var testFile = Path.Combine(dir, ".write_test");
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
