namespace Sa.Media.FFmpeg.Services;

using System;
using System.Runtime.InteropServices;

static class Constants
{
    public const string FFmpegFileNameWin = "ffmpeg.exe";
    public const string FFmpegFileNameLinux = "ffmpeg";
    public const string FFprobeFileNameWin = "ffprobe.exe";
    public const string FFprobeFileNameLinux = "ffprobe";

    public static TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public static bool IsOsWindows { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public static bool IsOsLinux { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    public static string FFmpegExecutableFileName { get; } = IsOsWindows ? FFmpegFileNameWin : FFmpegFileNameLinux;
    public static string FFprobeExecutableFileName { get; } = IsOsWindows ? FFprobeFileNameWin : FFprobeFileNameLinux;
}
