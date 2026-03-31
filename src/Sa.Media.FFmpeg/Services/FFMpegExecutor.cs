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

        var sampleRate = outputSampleRate.HasValue ? $"-ar {outputSampleRate}" : string.Empty;
        var channelCount = outputChannelCount.HasValue ? $"-ac {outputChannelCount}" : string.Empty;
        var cmd = $"{OverArg(isOverwrite)} {Constants.CleanBannerFlags} -i {QuotePath(inputFileName)} " +
            $"-acodec pcm_s16le -sample_fmt s16 {channelCount} {sampleRate} " +
            $"-f wav {Constants.CleanWavOutputFlags} {QuotePath(outputFileName)}";

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
        var sampleRate = outputSampleRate.HasValue ? $"-ar {outputSampleRate}" : string.Empty;
        var channelCount = outputChannelCount.HasValue ? $"-ac {outputChannelCount}" : string.Empty;
        var cmd = $"{Constants.CleanBannerFlags} -f {inputFormat} -i pipe:0 " +
            $"-acodec pcm_s16le -sample_fmt s16 {channelCount} {sampleRate} " +
            $"-f wav {Constants.CleanWavOutputFlags} pipe:1";

        await executor.ExecuteStdOutAsync(cmd, inputStream, onOutput, timeout: timeout, cancellationToken: cancellationToken);
    }

    public async Task<string> ConvertToMp3(
        string inputFileName,
        string outputFileName,
        bool isOverwrite = true,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        CheckFiles(inputFileName, outputFileName);

        var cmd = $"{OverArg(isOverwrite)} {Constants.CleanBannerFlags} -i {QuotePath(inputFileName)} " +
            $"-f mp3 {Libmp3lameArg()} -ar 16000 -b:a 128k {QuotePath(outputFileName)}";
        var result = await executor.ExecuteAsync(cmd, timeout: timeout, cancellationToken: cancellationToken);
        return result.StandardError;
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

        var cmd = $"{OverArg(isOverwrite)} {Constants.CleanBannerFlags} -i {QuotePath(inputFileName)} " +
            $"-f ogg {LibopuArg(isLibopus)} {QuotePath(outputFileName)}";
        var result = await executor.ExecuteAsync(cmd, timeout: timeout, cancellationToken: cancellationToken);
        return result.StandardError;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CheckFiles(string inputFileName, string outputFileName)
    {
        if (string.Equals(Path.GetFullPath(inputFileName), Path.GetFullPath(outputFileName), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Input and output files must be different", nameof(outputFileName));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string Libmp3lameArg() => Constants.IsOsLinux ? "-c:a libmp3lame" : string.Empty;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string OverArg(bool isOverwrite) => isOverwrite ? "-y" : string.Empty;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string LibopuArg(bool isLibopus)
    {
        if (!Constants.IsOsLinux) return string.Empty;
        return isLibopus ? "-c:a libopus" : "-c:a libvorbis";
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string QuotePath(string path) => $"\"{path}\"";
}
