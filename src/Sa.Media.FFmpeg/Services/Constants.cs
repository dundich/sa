namespace Sa.Media.FFmpeg.Services;

using System;
using System.Runtime.InteropServices;

static class Constants
{
    public const string FFmpegFileNameWin = "ffmpeg.exe";
    public const string FFmpegFileNameLinux = "ffmpeg";
    public const string FFprobeFileNameWin = "ffprobe.exe";
    public const string FFprobeFileNameLinux = "ffprobe";

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    public static bool IsOsWindows { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public static bool IsOsLinux { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    public static bool IsOsMacOs { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    /// <summary>
    /// Time to wait for a process to exit gracefully before forcing it to kill.
    /// </summary>
    public const int ShutdownGracePeriodMs = 500;

    /// <summary>
    /// Time to wait for a process to exit after Kill() is called.
    /// </summary>
    public const int ShutdownKillTimeoutMs = 2000;

    public static string FFmpegExecutableFileName { get; } = IsOsWindows ? FFmpegFileNameWin : FFmpegFileNameLinux;
    public static string FFprobeExecutableFileName { get; } = IsOsWindows ? FFprobeFileNameWin : FFprobeFileNameLinux;


    public const string CleanBannerFlags = "-hide_banner -loglevel error";

    public const string CleanWavOutputFlags = "-map_metadata -1 -write_bext 0 -bitexact -fflags +bitexact";

    public const int StringBuilderInitialCapacity = 512;
}
