using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Sa.Media.FFmpeg.Services;

internal sealed class FFProbeExecutor(
    IFFRawExecutor executor,
    ILogger<FFProbeExecutor>? logger = null) : IFFProbeExecutor
{
    public IFFRawExecutor Executor => executor;

    public async Task<(int? channels, int? sampleRate)> GetChannelsAndSampleRate(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"File not found: '{filePath}'.", filePath);

        // -select_streams a:0 обязателен. Без него ffprobe печатает параметры КАЖДОГО потока
        // подряд, и наш парсер заканчивал на последнем — то есть на дорожке с чужом языке.
        var result = await executor.ExecuteAsync(
            $"-v error -select_streams a:0 -show_entries stream=channels,sample_rate -of default=nw=1 {FFmpegArgs.Quote(filePath)}",
            throwOnError: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Ненулевой код уже разобран throwOnError; ветка ниже оставлена как защита от
        // IFFRawExecutor-реализаций, которые код возврата игнорируют.
        if (result.ExitCode != 0)
            throw new ProcessExecutionResultException(result);

        return FFOutputParser.ParseChannelsAndSampleRate(result.StandardOutput);
    }

    public async Task<MediaMetadata> GetMetaInfo(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"File not found: '{filePath}'.", filePath);

        // Раньше здесь стоял throwOnError: false по умолчанию: на битом/отсутствующем файле
        // возвращался MediaMetadata.Empty, и вызывающий принимал это за «файл без метаданных».
        var output = await executor.ExecuteAsync(
            $"-v quiet -print_format json -show_streams -show_format {FFmpegArgs.Quote(filePath)}",
            throwOnError: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return Parse(output.StandardOutput, filePath);
    }

    public async Task<MediaMetadata> GetMetaInfo(Stream audioStream, string inputFormat, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audioStream);
        FFmpegArgs.ValidateFormatName(inputFormat, nameof(inputFormat));

        MediaMetadata metadata = MediaMetadata.Empty;

        await executor.ExecuteStdOutAsync(
            $"-v quiet -print_format json -show_streams -show_format -f {inputFormat} -i pipe:0",
            audioStream,
            async (onOutput, ct) =>
            {
                try
                {
                    var metaDataInfo = await JsonSerializer.DeserializeAsync<FFProbeMetaDataInfo>(
                        onOutput,
                        FFmpegJsonSerializerContext.Default.FFProbeMetaDataInfo,
                        cancellationToken: ct).ConfigureAwait(false);

                    metadata = ToMetadata(metaDataInfo);
                }
                catch (JsonException jsonEx)
                {
                    // Потоковый вариант: если JSON не распознан, исключение наружу не выходит —
                    // ExecuteStdOutAsync уже проверил код возврата, а «пустые метаданные» здесь
                    // означают лишь, что ffprobe нечего сообщить. Логируем и возвращаем пустое.
                    logger?.LogWarning(
                        jsonEx,
                        "Failed to deserialize FFprobe JSON output from a '{InputFormat}' stream. Returning empty metadata.",
                        inputFormat);
                    metadata = MediaMetadata.Empty;
                }
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return metadata;
    }

    MediaMetadata Parse(string json, string filePath)
    {
        try
        {
            var metaDataInfo = JsonSerializer.Deserialize(
                json,
                FFmpegJsonSerializerContext.Default.FFProbeMetaDataInfo);

            return ToMetadata(metaDataInfo);
        }
        catch (JsonException jsonEx)
        {
            // JSON невалиден — это уже настоящая ошибка, а не «нет метаданных».
            logger?.LogWarning(jsonEx, "Failed to deserialize FFprobe JSON output for '{FilePath}'.", filePath);
            throw new ProcessExecutionException(
                0,
                $"FFprobe returned output that is not valid JSON for '{filePath}'. First 512 chars: {json.AsSpan(0, Math.Min(512, json.Length))}",
                jsonEx);
        }
    }

    static MediaMetadata ToMetadata(FFProbeMetaDataInfo? metaDataInfo) => new(
        Duration: metaDataInfo?.Format?.Duration.StrToDouble(),
        FormatName: metaDataInfo?.Format?.FormatName,
        BitRate: metaDataInfo?.Format?.BitRate.StrToInt(),
        Size: metaDataInfo?.Format?.Size.StrToLong());
}
