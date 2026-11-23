namespace Sa.Media.FFmpeg.Services;

internal sealed class PcmS16LeChannelManipulator(
    IFFMpegExecutor? ffmpeg = null,
    IFFProbeExecutor? ffprobe = null,
    string? writableDirectory = null)
    : IPcmS16LeChannelManipulator
{
    private readonly IFFMpegExecutor _ffmpeg = ffmpeg ?? IFFMpegExecutor.Default;
    private readonly IFFProbeExecutor _ffprobe = ffprobe ?? IFFProbeExecutor.Default;
    private readonly string? _writableDirectory =
        string.IsNullOrWhiteSpace(writableDirectory) ? null : writableDirectory;

    /// <summary>
    /// Разрешает имя выходного файла так же, как <see cref="FFMpegExecutor"/>:
    /// иначе моно-ветка <see cref="SplitAsync"/> писала в <c>WritableDirectory</c>, а стерео-ветка
    /// (raw-executor, без <see cref="OutputPaths"/>) — в текущий каталог процесса.
    /// </summary>
    private string ResolveOutput(string outputFileName) => OutputPaths.Resolve(outputFileName, _writableDirectory);

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

        // Суффикс подставляется в имя выходного файла, а значит попадает в командную строку.
        FFmpegArgs.ValidateFileNameToken(channelSuffix, nameof(channelSuffix));

        // Получаем количество каналов из метаданных
        var (channels, _) = await _ffprobe.GetChannelsAndSampleRateAsync(inputFileName, cancellationToken)
            .ConfigureAwait(false);

        if (!channels.HasValue || channels.Value <= 0)
        {
            throw new InvalidOperationException(
                $"Failed to determine the number of audio channels in '{inputFileName}'.");
        }

        if (channels > 2)
        {
            throw new NotSupportedException(
                $"Only mono (1 channel) and stereo (2 channels) audio formats are supported, but '{inputFileName}' has {channels}.");
        }

        var outFileExtension = Path.GetExtension(outputFileName) is { Length: > 0 } ext ? ext : ".wav";

        // Сначала разрешаем базовое имя (возможно, относительно WritableDirectory), и только
        // потом строим имена каналов: иначе возвращённые пути не совпадали бы с созданными файлами.
        string resolvedBase = ResolveOutput(outputFileName);
        string outFilePrefix = Path.Combine(
            Path.GetDirectoryName(resolvedBase) ?? string.Empty,
            Path.GetFileNameWithoutExtension(resolvedBase));

        if (channels == 1)
        {
            var file0 = $"{outFilePrefix}{channelSuffix}0{outFileExtension}";
            await _ffmpeg.ConvertToPcmS16LeAsync(
                inputFileName,
                file0,
                outputSampleRate,
                1,
                isOverwrite,
                timeout,
                cancellationToken).ConfigureAwait(false);
            return [file0];
        }

        var files = new[]
        {
            $"{outFilePrefix}{channelSuffix}0{outFileExtension}",
            $"{outFilePrefix}{channelSuffix}1{outFileExtension}"
        };

        var cmd = BuildSplitCommand(inputFileName, files, outputSampleRate, isOverwrite);

        // throwOnError: true — раньше ошибка FFmpeg проглатывалась, и метод возвращал пути к
        // файлам, которых не существует. То же касается JoinAsync.
        _ = await _ffmpeg.Executor.ExecuteAsync(
            cmd,
            throwOnError: true,
            timeout: timeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);

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

        if (!File.Exists(leftFileName))
            throw new FileNotFoundException($"Left input file not found: '{leftFileName}'.", leftFileName);
        if (!File.Exists(rightFileName))
            throw new FileNotFoundException($"Right input file not found: '{rightFileName}'.", rightFileName);

        // Как и в SplitAsync: имя без каталога уходит в WritableDirectory, а возвращаем уже
        // разрешённый путь — иначе вызывающий получал бы путь к файлу, которого нет.
        string resolvedOutput = ResolveOutput(outputFileName);
        var cmd = BuildJoinCommand(leftFileName, rightFileName, resolvedOutput, outputSampleRate, isOverwrite);

        _ = await _ffmpeg.Executor.ExecuteAsync(
            cmd,
            throwOnError: true,
            timeout: timeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return resolvedOutput;
    }

    #region Private command builders

    private static string BuildSplitCommand(
        string inputFileName,
        string[] outputFiles,
        int? outputSampleRate,
        bool isOverwrite)
    {
        var b = new ValueStringBuilder(Constants.StringBuilderInitialCapacity);
        b.Append(Constants.CleanBannerFlags);
        // Флаг перезаписи — после баннера, иначе он склеивается с ним в «-y-nostdin».
        b.Append(isOverwrite ? " -y " : " -n ");
        b.Append(" -i ");
        FFmpegArgs.AppendQuoted(ref b, inputFileName);
        b.Append(" -filter_complex \"[0:a]channelsplit=channel_layout=stereo[left][right]\" ");
        b.Append($"-map \"[left]\" {WavOutput(outputSampleRate, outputFiles[0])} ");
        b.Append($"-map \"[right]\" {WavOutput(outputSampleRate, outputFiles[1])}");
        return b.ToString();
    }

    private static string BuildJoinCommand(
        string leftFileName,
        string rightFileName,
        string outputFileName,
        int? outputSampleRate,
        bool isOverwrite)
    {
        var b = new ValueStringBuilder(Constants.StringBuilderInitialCapacity);
        b.Append(Constants.CleanBannerFlags);
        b.Append(isOverwrite ? " -y " : " -n ");
        b.Append(" -i ");
        FFmpegArgs.AppendQuoted(ref b, leftFileName);
        b.Append(" -i ");
        FFmpegArgs.AppendQuoted(ref b, rightFileName);
        b.Append(" -filter_complex \"[0:a][1:a]amerge=inputs=2[a]\" -map \"[a]\" -ac 2 ");
        b.Append($"-acodec pcm_s16le -sample_fmt s16 {SampleRate(outputSampleRate)} -f wav ");
        b.Append(Constants.CleanWavOutputFlags);
        b.Append(' ');
        FFmpegArgs.AppendQuoted(ref b, outputFileName);
        return b.ToString();
    }

    /// <summary>
    /// Общий кусок для обоих выходов SplitAsync.
    /// Раньше здесь стоял «-f wav» без CleanWavOutputFlags, и на выходе получался WAV с
    /// LIST/INFO и data-размером 0xFFFFFFFF — то есть не воспроизводимый побайтово файл,
    /// в отличие от JoinAsync и всех ConvertToPcm*, которые эти флаги ставили.
    /// </summary>
    static string WavOutput(int? outputSampleRate, string outputFile)
    {
        var result = $"-acodec pcm_s16le -ac 1 -sample_fmt s16 {SampleRate(outputSampleRate)} -f wav {Constants.CleanWavOutputFlags} ";
        return result + FFmpegArgs.Quote(outputFile);
    }

    static string SampleRate(int? outputSampleRate) => outputSampleRate is { } r ? $"-ar {r}" : string.Empty;

    #endregion
}
