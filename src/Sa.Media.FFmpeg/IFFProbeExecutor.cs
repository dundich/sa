using Sa.Media.FFmpeg.Services;

namespace Sa.Media.FFmpeg;

public interface IFFProbeExecutor
{
    public IFFRawExteсutor Exteсutor { get; }

    Task<(int? channels, int? sampleRate)> GetChannelsAndSampleRate(string filePath, CancellationToken cancellationToken = default);
    Task<MediaMetadata> GetMetaInfo(string filePath, CancellationToken cancellationToken = default);

    public static IFFProbeExecutor Default { get; } = new FFMpegExecutorFactory().CreateFFProbeExecutor();
}
