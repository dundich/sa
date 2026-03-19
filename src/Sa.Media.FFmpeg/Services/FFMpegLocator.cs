using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Sa.Media.FFmpeg.Services;

internal sealed class FFMpegLocator : IFFMpegLocator
{
    const string PlatformFolder = "sa/native";

    /// <summary>
    /// Находит путь к ffmpeg-исполняемому файлу.
    /// </summary>
    public string FindFFmpegExecutablePath()
    {
        var filePath = FindFFmpeg();

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            MakeFileExecutable(filePath);
            var destDir = Path.GetDirectoryName(filePath);
            MakeFileExecutable(Path.Combine(destDir!, Constants.FFprobeFileNameLinux));
        }

        return filePath;
    }

    private static string FindFFmpeg()
    {
        var executableName = Constants.FFmpegExecutableFileName;

        var appDir = AppContext.BaseDirectory;

        // 1. current dir
        var fullPath = Path.Combine(appDir, executableName);
        if (File.Exists(fullPath))
            return fullPath;

        // 2. (runtimes/native)
        fullPath = Path.Combine(appDir, PlatformFolder, executableName);
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

        throw new InvalidOperationException($"ffmpeg not found.");
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
}
