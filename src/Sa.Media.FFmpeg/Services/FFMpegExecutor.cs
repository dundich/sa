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
        var overwrite = isOverwrite ? "-y" : string.Empty;
        
        var result = await exteсutor.ExecuteAsync(
            $"{overwrite} -i \"{inputFileName}\" -acodec pcm_s16le {channelCount} {sampleRate} -f wav \"{outputFileName}\"",
            cancellationToken: cancellationToken);
        
        return result.StandardError;
    }

    public async Task<string> ConvertToMp3(
        string inputFileName,
        string outputFileName,
        bool isOverwrite = false,
        CancellationToken cancellationToken = default)
    {
        var isOver = isOverwrite ? "-y" : string.Empty;
        var cmd = Constants.IsOsLinux
            ? $"{isOver} -i \"{inputFileName}\" -f mp3 -c:a libmp3lame \"{outputFileName}\""
            : $"{isOver} -i \"{inputFileName}\" -f mp3 \"{outputFileName}\"";

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
        var isOver = isOverwrite ? "-y" : string.Empty;
        var codec = isLibopus ? "libopus" : "libvorbis";
        var cmd = Constants.IsOsLinux
            ? $"{isOver} -i \"{inputFileName}\" -f ogg -c:a {codec} \"{outputFileName}\""
            : $"{isOver} -i \"{inputFileName}\" -f ogg \"{outputFileName}\"";

        var result = await exteсutor.ExecuteAsync(cmd, cancellationToken: cancellationToken);
        return result.StandardError;
    }
}
