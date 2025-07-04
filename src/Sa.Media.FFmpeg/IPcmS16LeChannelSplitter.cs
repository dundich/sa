namespace Sa.Media.FFmpeg;

public interface IPcmS16LeChannelSplitter
{
    Task<IReadOnlyList<string>> SplitAsync(string inputFileName, string outputFileName, int? outputSampleRate = null, string channelSuffix = "_channel_", bool isOverwrite = false, TimeSpan? timeout = null, CancellationToken cancellationToken = default);
}
