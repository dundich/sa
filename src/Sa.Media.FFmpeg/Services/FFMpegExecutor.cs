using System.Runtime.CompilerServices;

namespace Sa.Media.FFmpeg.Services;

internal sealed class FFMpegExecutor(IFFRawExecutor executor) : IFFMpegExecutor
{
    public IFFRawExecutor Executor => executor;

    public async Task<string> GetVersion(CancellationToken cancellationToken = default)
    {
        var result = await executor.ExecuteAsync("-version", cancellationToken: cancellationToken);
        return result.StandardOutput;
    }

    public async Task<string> GetFormats(CancellationToken cancellationToken = default)
    {
        var result = await executor.ExecuteAsync("-formats", cancellationToken: cancellationToken);
        return result.StandardOutput;
    }

    public async Task<string> GetCodecs(CancellationToken cancellationToken = default)
    {
        var result = await executor.ExecuteAsync("-codecs", cancellationToken: cancellationToken);
        return result.StandardOutput;
    }

    public async Task<string> ConvertToPcmS16Le(
        string inputFileName,
        string outputFileName,
        int? outputSampleRate = 16000,
        ushort? outputChannelCount = null,
        bool isOverwrite = true,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        CheckFiles(inputFileName, outputFileName);

        string cmd = GetCmdToPcm16Le(inputFileName, outputFileName, outputSampleRate, outputChannelCount, isOverwrite);

        var result = await executor.ExecuteAsync(cmd, timeout: timeout, cancellationToken: cancellationToken);
        return result.StandardError;
    }

    public async Task ConvertToPcmS16Le(
        Stream inputStream,
        string inputFormat,
        Func<Stream, CancellationToken, Task> onOutput,
        int? outputSampleRate = 16000,
        ushort? outputChannelCount = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        string cmd = GetCmdToPcmS16Le(inputFormat, outputSampleRate, outputChannelCount);

        await executor.ExecuteStdOutAsync(
            cmd,
            inputStream,
            onOutput,
            timeout: timeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static string GetCmdToPcmS16Le(string inputFormat, int? outputSampleRate, ushort? outputChannelCount)
    {
        using var b = new ValueStringBuilder(Constants.StringBuilderInitialCapacity);
        b.Append(Constants.CleanBannerFlags);
        b.Append(" -f ");
        b.Append(inputFormat);
        b.Append(" -i pipe:0 ");
        b.Append(" -acodec pcm_s16le -sample_fmt s16 ");

        if (outputChannelCount.HasValue)
        {
            b.Append(" -ac ");
            b.Append(outputChannelCount.Value);
        }

        if (outputSampleRate.HasValue)
        {
            b.Append(" -ar ");
            b.Append(outputSampleRate.Value);
        }

        b.Append(" -f wav -map 0:a:0 ");
        b.Append(Constants.CleanWavOutputFlags);
        b.Append(" pipe:1");
        return b.ToString();
    }

    private static string GetCmdToPcm16Le(
    string inputFileName,
    string outputFileName,
    int? outputSampleRate,
    ushort? outputChannelCount,
    bool isOverwrite)
    {
        using var b = new ValueStringBuilder(Constants.StringBuilderInitialCapacity);
        if (isOverwrite)
        {
            b.Append("-y ");
        }

        b.Append(Constants.CleanBannerFlags);
        b.Append(" -i \"");
        b.Append(inputFileName);
        b.Append('"');
        b.Append(" -c:a pcm_s16le ");

        if (outputSampleRate.HasValue)
        {
            b.Append(" -ar ");
            b.Append(outputSampleRate.Value);
        }

        if (outputChannelCount.HasValue)
        {
            b.Append(" -ac ");
            b.Append(outputChannelCount.Value);
        }

        b.Append(" -f wav ");
        b.Append('"');
        b.Append(outputFileName);
        b.Append('"');

        return b.ToString();
    }

    public async Task<string> ConvertToMp3(
        string inputFileName,
        string outputFileName,
        bool isOverwrite = true,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        CheckFiles(inputFileName, outputFileName);

        string cmd = GetCmdToMp3(inputFileName, outputFileName, isOverwrite);

        var result = await executor.ExecuteAsync(cmd, timeout: timeout, cancellationToken: cancellationToken);
        return result.StandardError;
    }

    private static string GetCmdToMp3(string inputFileName, string outputFileName, bool isOverwrite)
    {
        using var b = new ValueStringBuilder(Constants.StringBuilderInitialCapacity);
        if (isOverwrite)
        {
            b.Append("-y ");
        }

        b.Append(Constants.CleanBannerFlags);
        b.Append(" -i \"");
        b.Append(inputFileName);
        b.Append('"');
        b.Append(" -f mp3 -map 0:a:0 ");

        if (Constants.IsOsLinux)
        {
            b.Append(" -c:a libmp3lame ");
        }

        b.Append(" -ar 16000 -b:a 128k ");
        b.Append('"');
        b.Append(outputFileName);
        b.Append('"');

        return b.ToString();
    }

    public async Task<string> ConvertToOgg(
        string inputFileName,
        string outputFileName,
        bool isLibopus = false,
        bool isOverwrite = true,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        CheckFiles(inputFileName, outputFileName);
        string cmd = GetCmdToOgg(inputFileName, outputFileName, isLibopus, isOverwrite);
        var result = await executor.ExecuteAsync(cmd, timeout: timeout, cancellationToken: cancellationToken);
        return result.StandardError;
    }

    private static string GetCmdToOgg(string inputFileName, string outputFileName, bool isLibopus, bool isOverwrite)
    {
        using var b = new ValueStringBuilder(Constants.StringBuilderInitialCapacity);
        if (isOverwrite)
        {
            b.Append(" -y ");
        }

        b.Append(Constants.CleanBannerFlags);
        b.Append(" -i \"");
        b.Append(inputFileName);
        b.Append('"');
        b.Append(" -f ogg -map 0:a:0 ");

        if (Constants.IsOsLinux)
        {
            b.Append(' ');
            b.Append(isLibopus ? "-c:a libopus" : "-c:a libvorbis");
        }

        b.Append(' ');
        b.Append('"');
        b.Append(outputFileName);
        b.Append('"');

        return b.ToString();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CheckFiles(string inputFileName, string outputFileName)
    {
        if (string.Equals(Path.GetFullPath(inputFileName), Path.GetFullPath(outputFileName), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Input and output files must be different", nameof(outputFileName));
    }

    public async Task<string> ConvertToPcmS16LePreservingFormat(
        string inputFileName,
        string outputFileName,
        bool isOverwrite = true,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        return await ConvertToPcmS16Le(
            inputFileName,
            outputFileName,
            outputSampleRate: null,
            outputChannelCount: null,
            isOverwrite,
            timeout,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ConvertToPcmS16LeRaw(
        string inputFileName,
        string outputFileName,
        int? outputSampleRate = 16000,
        ushort? outputChannelCount = null,
        bool isOverwrite = true,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        CheckFiles(inputFileName, outputFileName);

        var cmd = GetCmdToPcmS16LeRaw(inputFileName, outputFileName, outputSampleRate, outputChannelCount, isOverwrite);

        var result = await executor.ExecuteAsync(cmd, timeout: timeout, cancellationToken: cancellationToken);
        return result.StandardError;
    }

    public async Task ConvertToPcmS16LeRaw(
        Stream inputStream,
        string inputFormat,
        Func<Stream, CancellationToken, Task> onOutput,
        int? outputSampleRate = 16000,
        ushort? outputChannelCount = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        string cmd = GetCmdPcmS16LeRawStream(inputFormat, outputSampleRate, outputChannelCount);

        await executor.ExecuteStdOutAsync(
            cmd,
            inputStream,
            onOutput,
            timeout: timeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static string GetCmdToPcmS16LeRaw(
        string inputFileName,
        string outputFileName,
        int? outputSampleRate,
        ushort? outputChannelCount,
        bool isOverwrite)
    {
        using var b = new ValueStringBuilder(Constants.StringBuilderInitialCapacity);
        if (isOverwrite)
        {
            b.Append(" -y ");
        }

        b.Append(Constants.CleanBannerFlags);
        b.Append(" -i \"");
        b.Append(inputFileName);
        b.Append('"');
        b.Append(" -c:a pcm_s16le ");

        if (outputSampleRate.HasValue)
        {
            b.Append(" -ar ");
            b.Append(outputSampleRate.Value);
        }

        if (outputChannelCount.HasValue)
        {
            b.Append(" -ac ");
            b.Append(outputChannelCount.Value);
        }

        b.Append(" -f s16le ");
        b.Append('"');
        b.Append(outputFileName);
        b.Append('"');
        return b.ToString();
    }

    private static string GetCmdPcmS16LeRawStream(
        string inputFormat,
        int? outputSampleRate,
        ushort? outputChannelCount)
    {
        using var b = new ValueStringBuilder(Constants.StringBuilderInitialCapacity);
        b.Append(Constants.CleanBannerFlags);
        b.Append(" -f ");
        b.Append(inputFormat);
        b.Append(" -i pipe:0 ");
        b.Append(" -c:a pcm_s16le ");

        if (outputChannelCount.HasValue)
        {
            b.Append(" -ac ");
            b.Append(outputChannelCount.Value);
        }

        if (outputSampleRate.HasValue)
        {
            b.Append(" -ar ");
            b.Append(outputSampleRate.Value);
        }

        b.Append(" -f s16le pipe:1 ");

        return b.ToString();
    }

    public async Task<string> ConvertToPcmS32Le(
        string inputFileName,
        string outputFileName,
        int? outputSampleRate = 16000,
        ushort? outputChannelCount = null,
        bool isOverwrite = true,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        CheckFiles(inputFileName, outputFileName);

        string cmd = GetCmdToPcmS32Le(inputFileName, outputFileName, outputSampleRate, outputChannelCount, isOverwrite);

        var result = await executor.ExecuteAsync(cmd, timeout: timeout, cancellationToken: cancellationToken);
        return result.StandardError;
    }

    private static string GetCmdToPcmS32Le(
        string inputFileName,
        string outputFileName,
        int? outputSampleRate,
        ushort? outputChannelCount,
        bool isOverwrite)
    {
        using var b = new ValueStringBuilder(Constants.StringBuilderInitialCapacity);
        if (isOverwrite)
        {
            b.Append(" -y ");
        }

        b.Append(Constants.CleanBannerFlags);
        b.Append(" -i \"");
        b.Append(inputFileName);
        b.Append('"');
        b.Append(" -c:a pcm_s32le ");

        if (outputSampleRate.HasValue)
        {
            b.Append(" -ar ");
            b.Append(outputSampleRate.Value);
        }

        if (outputChannelCount.HasValue)
        {
            b.Append(" -ac ");
            b.Append(outputChannelCount.Value);
        }

        b.Append(" -f wav ");
        b.Append('"');
        b.Append(outputFileName);
        b.Append('"');
        return b.ToString();
    }

    public async Task<string> ConvertToPcmF32Le(
        string inputFileName,
        string outputFileName,
        int? outputSampleRate = 16000,
        ushort? outputChannelCount = null,
        bool isOverwrite = true,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        CheckFiles(inputFileName, outputFileName);

        var cmd = GetCmdToPcmF32Le(inputFileName, outputFileName, outputSampleRate, outputChannelCount, isOverwrite);

        var result = await executor.ExecuteAsync(cmd, timeout: timeout, cancellationToken: cancellationToken);
        return result.StandardError;
    }

    private static string GetCmdToPcmF32Le(
        string inputFileName,
        string outputFileName,
        int? outputSampleRate,
        ushort? outputChannelCount,
        bool isOverwrite)
    {
        using var b = new ValueStringBuilder(Constants.StringBuilderInitialCapacity);
        if (isOverwrite)
        {
            b.Append(" -y ");
        }

        b.Append(Constants.CleanBannerFlags);
        b.Append(" -i \"");
        b.Append(inputFileName);
        b.Append('"');
        b.Append(" -c:a pcm_f32le ");

        if (outputSampleRate.HasValue)
        {
            b.Append(" -ar ");
            b.Append(outputSampleRate.Value);
        }

        if (outputChannelCount.HasValue)
        {
            b.Append(" -ac ");
            b.Append(outputChannelCount.Value);
        }

        b.Append(" -f wav ");
        b.Append('"');
        b.Append(outputFileName);
        b.Append('"');
        return b.ToString();
    }

    public async Task<string> ConvertToPcmF32LeRaw(
        string inputFileName,
        string outputFileName,
        int? outputSampleRate = 16000,
        ushort? outputChannelCount = null,
        bool isOverwrite = true,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        CheckFiles(inputFileName, outputFileName);

        var cmd = GetCmdToPcmF32LeRaw(inputFileName, outputFileName, outputSampleRate, outputChannelCount, isOverwrite);

        var result = await executor.ExecuteAsync(cmd, timeout: timeout, cancellationToken: cancellationToken);
        return result.StandardError;
    }

    public async Task ConvertToPcmF32LeRaw(
        Stream inputStream,
        string inputFormat,
        Func<Stream, CancellationToken, Task> onOutput,
        int? outputSampleRate = 16000,
        ushort? outputChannelCount = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        string cmd = GetCmdPcmF32LeRawStream(inputFormat, outputSampleRate, outputChannelCount);

        await executor.ExecuteStdOutAsync(
            cmd,
            inputStream,
            onOutput,
            timeout: timeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static string GetCmdToPcmF32LeRaw(
        string inputFileName,
        string outputFileName,
        int? outputSampleRate,
        ushort? outputChannelCount,
        bool isOverwrite)
    {
        using var b = new ValueStringBuilder(Constants.StringBuilderInitialCapacity);
        if (isOverwrite)
        {
            b.Append(" -y ");
        }

        b.Append(Constants.CleanBannerFlags);
        b.Append(" -i \"");
        b.Append(inputFileName);
        b.Append('"');
        b.Append(" -c:a pcm_f32le ");

        if (outputSampleRate.HasValue)
        {
            b.Append(" -ar ");
            b.Append(outputSampleRate.Value);
        }

        if (outputChannelCount.HasValue)
        {
            b.Append(" -ac ");
            b.Append(outputChannelCount.Value);
        }

        b.Append(" -f f32le ");
        b.Append('"');
        b.Append(outputFileName);
        b.Append('"');
        return b.ToString();
    }

    private static string GetCmdPcmF32LeRawStream(
        string inputFormat,
        int? outputSampleRate,
        ushort? outputChannelCount)
    {
        using var b = new ValueStringBuilder(Constants.StringBuilderInitialCapacity);
        b.Append(Constants.CleanBannerFlags);
        b.Append(" -f ");
        b.Append(inputFormat);
        b.Append(" -i pipe:0 ");
        b.Append(" -c:a pcm_f32le ");

        if (outputChannelCount.HasValue)
        {
            b.Append(" -ac ");
            b.Append(outputChannelCount.Value);
        }

        if (outputSampleRate.HasValue)
        {
            b.Append(" -ar ");
            b.Append(outputSampleRate.Value);
        }

        b.Append(" -f f32le pipe:1 ");

        return b.ToString();
    }
}
