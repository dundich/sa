using Sa.Extensions;
using System.Text.Json;

namespace Sa.Media.FFmpeg.Services;

internal class FFProbeExecutor(IFFProcessExteсutor exteсutor) : IFFProbeExecutor
{
    public IFFProcessExteсutor Exteсutor => exteсutor;

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
}
