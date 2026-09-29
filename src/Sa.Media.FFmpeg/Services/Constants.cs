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


    /// <summary>
    /// Общие флаги для всех вызовов FFmpeg.
    /// <list type="bullet">
    /// <item><c>-nostdin</c> обязателен: без него FFmpeg читает stdin в интерактивном режиме и
    /// на <c>isOverwrite: false</c> печатает <c>File '...' already exists. Overwrite? [y/N]</c>
    /// и блокируется до ответа. У нас stdin перенаправлен из буфера вызывающего, поэтому
    /// «ответа» не будет — процесс зависает до таймаута.</item>
    /// </list>
    /// </summary>
    public const string CleanBannerFlags = "-nostdin -hide_banner -loglevel error";

    /// <summary>Флаги, делающие WAV побайтово воспроизводимым (без LIST/INFO и с известным data-размером).</summary>
    public const string CleanWavOutputFlags = "-map_metadata -1 -write_bext 0 -bitexact -fflags +bitexact";

    public const int StringBuilderInitialCapacity = 512;
}
