using Sa.Extensions;
using System.Text.Json;

namespace Sa.Media.FFmpeg.Services;

internal class FFProbeExecutor(IFFRawExteсutor exteсutor) : IFFProbeExecutor
{
    public IFFRawExteсutor Exteсutor => exteсutor;

    public async Task<(int? channels, int? sampleRate)> GetChannelsAndSampleRate(string filePath, CancellationToken cancellationToken = default)
    {
        var result = await exteсutor.ExecuteAsync(
            $"-v error -show_entries stream=channels,sample_rate -of default=nw=1 \"{filePath}\"",
            cancellationToken: cancellationToken);

        string output = result.StandardOutput;

        return FFOutputParser.ParseChannelsAndSampleRate(output);
    }

    public async Task<MediaMetadata> GetMetaInfo(string filePath, CancellationToken cancellationToken = default)
    {
        var output = await exteсutor.ExecuteAsync(
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

        await exteсutor.ExecuteStdOutAsync(
            $"-v quiet -print_format json -show_streams -show_format -f {inputFormat} -i pipe:0",
            audioStream,
            async (onOutput, ct) =>
            {
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
                catch
                {
                    metadata = MediaMetadata.Empty;
                }
            },
            cancellationToken: cancellationToken);

        return metadata;
    }
}
