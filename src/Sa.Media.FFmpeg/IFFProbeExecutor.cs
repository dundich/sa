using Sa.Media.FFmpeg.Services;

namespace Sa.Media.FFmpeg;

/// <summary>
/// Interface for executing FFprobe commands to extract audio/video metadata.
/// </summary>
public interface IFFProbeExecutor
{
    /// <summary>
    /// Gets the raw executor used to run FFprobe commands.
    /// </summary>
    IFFRawExteсutor Exteсutor { get; }

    /// <summary>
    /// Retrieves the number of audio channels and sample rate from the specified media file.
    /// </summary>
    /// <param name="filePath">Path to the media file.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A tuple containing the number of channels and the sample rate (both may be null).</returns>
    Task<(int? channels, int? sampleRate)> GetChannelsAndSampleRate(
        string filePath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves detailed metadata information about the media file.
    /// </summary>
    /// <param name="filePath">Path to the media file.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A <see cref="MediaMetadata"/> object containing metadata.</returns>
    Task<MediaMetadata> GetMetaInfo(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves detailed metadata information about the media file.
    /// </summary>
    Task<MediaMetadata> GetMetaInfo(Stream audioStream, string inputFormat, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the default implementation of the <see cref="IFFProbeExecutor"/> interface.
    /// </summary>
    public static IFFProbeExecutor Default { get; } = new FFMpegExecutorFactory().CreateFFProbeExecutor();
}
