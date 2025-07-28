namespace Sa.Media.FFmpeg.Services;

internal sealed class FFMpegExecutor(IFFRawExteсutor exteсutor) : IFFMpegExecutor
{
    public IFFRawExteсutor Exteсutor => exteсutor;

    public async Task<string> GetVersion(CancellationToken cancellationToken = default)
    {
        var result = await exteсutor.ExecuteAsync("-version", cancellationToken: cancellationToken);
        return result.StandardOutput;
    }

    public async Task<string> GetFormats(CancellationToken cancellationToken = default)
    {
        var result = await exteсutor.ExecuteAsync("-formats", cancellationToken: cancellationToken);
        return result.StandardOutput;
    }

    public async Task<string> GetCodecs(CancellationToken cancellationToken = default)
    {
        var result = await exteсutor.ExecuteAsync("-codecs", cancellationToken: cancellationToken);
        return result.StandardOutput;
    }

    public async Task<string> ConvertToPcmS16Le(
        string inputFileName,
        string outputFileName,
        int? outputSampleRate = null,
        int? outputChannelCount = null,
        bool isOverwrite = false,
        CancellationToken cancellationToken = default)
    {
        var sampleRate = outputSampleRate.HasValue ? $"-ar {outputSampleRate}" : string.Empty;
        var channelCount = outputChannelCount.HasValue ? "-ac 1" : string.Empty;

        var result = await exteсutor.ExecuteAsync(
            $"{OverArg(isOverwrite)} -i {QuotePath(inputFileName)} -acodec pcm_s16le {channelCount} {sampleRate} -f wav {QuotePath(outputFileName)}",
            cancellationToken: cancellationToken);

        return result.StandardError;
    }

    public async Task<string> ConvertToMp3(
        string inputFileName,
        string outputFileName,
        bool isOverwrite = false,
        CancellationToken cancellationToken = default)
    {
        var cmd = $"{OverArg(isOverwrite)} -i {QuotePath(inputFileName)} -f mp3 {Libmp3lameArg()} {QuotePath(outputFileName)}";
        var result = await exteсutor.ExecuteAsync(cmd, cancellationToken: cancellationToken);
        return result.StandardError;
    }

    public async Task<string> ConvertToOgg(
        string inputFileName,
        string outputFileName,
        bool isLibopus = false,
        bool isOverwrite = false,
        CancellationToken cancellationToken = default)
    {
        var cmd = $"{OverArg(isOverwrite)} -i {QuotePath(inputFileName)} -f ogg {LibopuArg(isLibopus)} {QuotePath(outputFileName)}";
        var result = await exteсutor.ExecuteAsync(cmd, cancellationToken: cancellationToken);
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
