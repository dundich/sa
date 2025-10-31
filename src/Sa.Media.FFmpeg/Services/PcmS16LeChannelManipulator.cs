namespace Sa.Media.FFmpeg.Services;

public class PcmS16LeChannelManipulator(IFFMpegExecutor? ffmpeg = null, IFFProbeExecutor? ffprobe = null) : IPcmS16LeChannelManipulator
{

    private readonly IFFMpegExecutor _ffmpeg = ffmpeg ?? IFFMpegExecutor.Default;
    private readonly IFFProbeExecutor _ffprobe = ffprobe ?? IFFProbeExecutor.Default;

    /// <summary>
    /// Splits the audio file into individual channels and saves each channel as a separate WAV file.
    /// </summary>
    public async Task<IReadOnlyList<string>> SplitAsync(
        string inputFileName,
        string outputFileName,
        int? outputSampleRate = null,
        string channelSuffix = "_channel_",
        bool isOverwrite = true,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(inputFileName);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(outputFileName);

        // Получаем количество каналов из метаданных
        var (channels, _) = await _ffprobe.GetChannelsAndSampleRate(inputFileName, cancellationToken);

        if (!channels.HasValue || channels.Value <= 0)
            throw new InvalidOperationException("Failed to determine the number of channels.");

        if (channels > 2)
        {
            throw new NotSupportedException("Only mono (1 channel) and stereo (2 channels) audio formats are supported.");
        }

        var outFileExtension = Path.GetExtension(outputFileName) ?? ".wav";

        string outFilePrefix = Path.Combine(
            Path.GetDirectoryName(outputFileName) ?? string.Empty,
            Path.GetFileNameWithoutExtension(outputFileName)
        );

        var file0 = $"{outFilePrefix}{channelSuffix}0{outFileExtension}";

        if (channels == 1)
        {
            await _ffmpeg.ConvertToPcmS16Le(
                inputFileName,
                file0,
                outputSampleRate,
                1,
                isOverwrite,
                timeout,
                cancellationToken);

            return [file0];
        }

        List<string> files = [
            $"{outFilePrefix}{channelSuffix}0{outFileExtension}",
            $"{outFilePrefix}{channelSuffix}1{outFileExtension}"
        ];

        string over = isOverwrite ? "-y" : string.Empty;
        var sampleRate = outputSampleRate.HasValue ? $"-ar {outputSampleRate}" : string.Empty;

        string cmd = $"{over} {Constants.CleanBannerFlags} -i \"{inputFileName}\" -filter_complex \"[0:a]channelsplit=channel_layout=stereo[left][right]\" -map \"[left]\" -acodec pcm_s16le {sampleRate} -f wav \"{files[0]}\" -map \"[right]\"  -acodec pcm_s16le -f wav \"{files[1]}\"";

        _ = await _ffmpeg.Executor.ExecuteAsync(
            cmd,
            captureErrorOutput: false,
            timeout: timeout,
            cancellationToken: cancellationToken);

        return files;
    }


    public async Task<string> JoinAsync(
        string leftFileName,
        string rightFileName,
        string outputFileName,
        int? outputSampleRate = null,
        bool isOverwrite = true,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(leftFileName);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(rightFileName);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(outputFileName);

        string over = isOverwrite ? "-y" : string.Empty;
        var sampleRate = outputSampleRate.HasValue ? $"-ar {outputSampleRate}" : string.Empty;

        string cmd = $"{over} {Constants.CleanBannerFlags} -i \"{leftFileName}\" -i \"{rightFileName}\" -filter_complex \"[0:a][1:a]amerge=inputs=2[a]\" -map \"[a]\" -ac 2 -acodec pcm_s16le {sampleRate} -f wav {Constants.CleanWavOutputFlags} \"{outputFileName}\"";

        _ = await _ffmpeg.Executor.ExecuteAsync(
            cmd,
            captureErrorOutput: false,
            timeout: timeout,
            cancellationToken: cancellationToken);

        return outputFileName;
    }
}
