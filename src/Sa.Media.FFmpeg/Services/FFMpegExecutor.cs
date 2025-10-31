namespace Sa.Media.FFmpeg.Services;

internal sealed class FFMpegExecutor(IFFRawExtecutor extecutor) : IFFMpegExecutor
{
    public IFFRawExtecutor Extecutor => extecutor;

    public async Task<string> GetVersion(CancellationToken cancellationToken = default)
    {
        var result = await extecutor.ExecuteAsync("-version", cancellationToken: cancellationToken);
        return result.StandardOutput;
    }

    public async Task<string> GetFormats(CancellationToken cancellationToken = default)
    {
        var result = await extecutor.ExecuteAsync("-formats", cancellationToken: cancellationToken);
        return result.StandardOutput;
    }

    public async Task<string> GetCodecs(CancellationToken cancellationToken = default)
    {
        var result = await extecutor.ExecuteAsync("-codecs", cancellationToken: cancellationToken);
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
        var sampleRate = outputSampleRate.HasValue ? $"-ar {outputSampleRate}" : string.Empty;
        var channelCount = outputChannelCount.HasValue ? $"-ac {outputChannelCount}" : string.Empty;
        var cmd = $"{OverArg(isOverwrite)} {Constants.CleanBannerFlags} -i {QuotePath(inputFileName)} -acodec pcm_s16le {channelCount} {sampleRate} -f wav {Constants.CleanWavOutputFlags} {QuotePath(outputFileName)}";

        var result = await extecutor.ExecuteAsync(cmd, timeout: timeout, cancellationToken: cancellationToken);

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
        var cmd = $"{Constants.CleanBannerFlags} -f {inputFormat} -i pipe:0 -acodec pcm_s16le {channelCount} {sampleRate} -f wav {Constants.CleanWavOutputFlags} pipe:1";

        await extecutor.ExecuteStdOutAsync(cmd, inputStream, onOutput, timeout: timeout, cancellationToken: cancellationToken);
    }

    public async Task<string> ConvertToMp3(
        string inputFileName,
        string outputFileName,
        bool isOverwrite = true,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var cmd = $"{OverArg(isOverwrite)} {Constants.CleanBannerFlags} -i {QuotePath(inputFileName)} -f mp3 {Libmp3lameArg()} -ar 16000 -b:a 128k {QuotePath(outputFileName)}";
        var result = await extecutor.ExecuteAsync(cmd, timeout:timeout, cancellationToken: cancellationToken);
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
        var cmd = $"{OverArg(isOverwrite)} {Constants.CleanBannerFlags} -i {QuotePath(inputFileName)} -f ogg {LibopuArg(isLibopus)} {QuotePath(outputFileName)}";
        var result = await extecutor.ExecuteAsync(cmd, timeout: timeout, cancellationToken: cancellationToken);
        return result.StandardError;
    }

    private static string Libmp3lameArg() => Constants.IsOsLinux ? "-c:a libmp3lame" : string.Empty;

    private static string OverArg(bool isOverwrite) => isOverwrite ? "-y" : string.Empty;

    private static string LibopuArg(bool isLibopus)
    {
        if (!Constants.IsOsLinux) return string.Empty;
        return isLibopus ? "-c:a libopus" : "-c:a libvorbis";
    }

    private static string QuotePath(string path) => $"\"{path}\"";
}
