using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Sa.Media.FFmpeg.Services;

internal sealed class FFProbeExecutor(
    IFFRawExtecutor extecutor,
    ILogger<FFProbeExecutor>? logger = null) : IFFProbeExecutor
{
    public IFFRawExtecutor Extecutor => extecutor;

    public async Task<(int? channels, int? sampleRate)> GetChannelsAndSampleRate(string filePath, CancellationToken cancellationToken = default)
    {
        var result = await extecutor.ExecuteAsync(
            $"-v error -show_entries stream=channels,sample_rate -of default=nw=1 \"{filePath}\"",
            cancellationToken: cancellationToken);

        string output = result.StandardOutput;

        return FFOutputParser.ParseChannelsAndSampleRate(output);
    }

    public async Task<MediaMetadata> GetMetaInfo(string filePath, CancellationToken cancellationToken = default)
    {
        var output = await extecutor.ExecuteAsync(
            $"-v quiet -print_format json -show_streams -show_format \"{filePath}\"",
            cancellationToken: cancellationToken);

        try
        {
            var metaDataInfo = JsonSerializer.Deserialize<FFProbeMetaDataInfo>(
                output.StandardOutput,
                FFmpegJsonSerializerContext.Default.FFProbeMetaDataInfo);

            return new MediaMetadata(
                Duration: metaDataInfo?.Format?.Duration.StrToDouble(),
                FormatName: metaDataInfo?.Format?.FormatName,
                BitRate: metaDataInfo?.Format?.BitRate.StrToInt(),
                Size: metaDataInfo?.Format?.Size.StrToInt()
            );
        }
        catch
        {
            return MediaMetadata.Empty;
        }
    }


    public async Task<MediaMetadata> GetMetaInfo(Stream audioStream, string inputFormat, CancellationToken cancellationToken = default)
    {
        MediaMetadata metadata = MediaMetadata.Empty;

        await extecutor.ExecuteStdOutAsync(
            $"-v quiet -print_format json -show_streams -show_format -f {inputFormat} -i pipe:0",
            audioStream,
            async (onOutput, ct) =>
            {
                try
                {
                    //using var streamReader = new StreamReader(onOutput);
                    //string content = streamReader.ReadToEnd();
                    //Debug.WriteLine(content);
                    try
                    {
                        var metaDataInfo = await JsonSerializer.DeserializeAsync<FFProbeMetaDataInfo>(
                            onOutput,
                            FFmpegJsonSerializerContext.Default.FFProbeMetaDataInfo,
                            cancellationToken: ct);

                        metadata = new MediaMetadata(
                            Duration: metaDataInfo?.Format?.Duration?.StrToDouble(),
                            FormatName: metaDataInfo?.Format?.FormatName,
                            BitRate: metaDataInfo?.Format?.BitRate.StrToInt(),
                            Size: metaDataInfo?.Format?.Size.StrToInt()
                        );
                    }
                    catch (JsonException jsonEx)
                    {
                        logger?.LogError(jsonEx, "Failed to deserialize FFprobe JSON output");
                        metadata = MediaMetadata.Empty;
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Error processing FFprobe metadata");
                    metadata = MediaMetadata.Empty;
                }
            },
            cancellationToken: cancellationToken);

        return metadata;
    }
}
