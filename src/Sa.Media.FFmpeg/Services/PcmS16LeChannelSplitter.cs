using System.Diagnostics;

namespace Sa.Media.FFmpeg.Services;

public class PcmS16LeChannelSplitter(IFFMpegExecutor ffmpeg, IFFProbeExecutor ffprobe) : IPcmS16LeChannelSplitter
{
    /// <summary>
    /// Splits the audio file into individual channels and saves each channel as a separate WAV file.
    /// </summary>
    public async Task<IReadOnlyList<string>> SplitAsync(
        string inputFileName,
        string outputFileName,
        int? outputSampleRate = null,
        string channelSuffix = "_channel_",
        bool isOverwrite = false,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(inputFileName);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(outputFileName);

        // Получаем количество каналов из метаданных
        var (channels, _) = await ffprobe.GetChannelsAndSampleRate(inputFileName, cancellationToken);

        if (!channels.HasValue || channels.Value <= 0)
            throw new InvalidOperationException("Failed to determine the number of channels.");

        string over = isOverwrite ? "-y" : string.Empty;
        var sampleRate = outputSampleRate.HasValue ? $"-ar {outputSampleRate}" : string.Empty;

        var outFileExtension = Path.GetExtension(outputFileName) ?? ".wav";

        string outFilePrefix = Path.Combine(
            Path.GetDirectoryName(outputFileName) ?? string.Empty,
            Path.GetFileNameWithoutExtension(outputFileName)
        );

        List<string> files = [
            $"{outFilePrefix}{channelSuffix}0{outFileExtension}",
            $"{outFilePrefix}{channelSuffix}1{outFileExtension}"
        ];

        string cmd = $"{over} -i \"{inputFileName}\" -filter_complex \"[0:a]channelsplit=channel_layout=stereo[left][right]\" -map \"[left]\" -acodec pcm_s16le {sampleRate} -f wav \"{files[0]}\" -map \"[right]\"  -acodec pcm_s16le -f wav \"{files[1]}\"";

        var output = await ffmpeg.Exteсutor.ExecuteAsync(
            cmd,
            captureErrorOutput: false,
            timeout: timeout,
            cancellationToken: cancellationToken);

        Debug.WriteLine(output);

        return files;
    }
}
