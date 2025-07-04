using Sa.Media.FFmpeg;

namespace Sa.Media.FFmpegTests;

public sealed class FFProbeProcessorTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    static IFFProbeExecutor Processor => IFFProbeExecutor.Default;

    [Theory]
    [InlineData(@".\data\input.mp3")]
    [InlineData(@".\data\input.wav")]
    [InlineData(@".\data\input.ogg")]
    public async Task GetAudioChannelCount_ShouldBeWork(string testFilePath)
    {
        // Act
        var (channels, sampleRate) = await Processor.GetChannelsAndSampleRate(testFilePath, cancellationToken: CancellationToken);
        Assert.Equal(2, channels);
        Assert.True(sampleRate > 0);
    }

    [Theory]
    [InlineData(@".\data\input.mp3")]
    [InlineData(@".\data\input.wav")]
    [InlineData(@".\data\input.ogg")]
    public async Task GetMetaInfo_CorrectlyReadsRealFile(string testFilePath)
    {
        // Act
        var result = await Processor.GetMetaInfo(testFilePath, CancellationToken);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Duration > 0);
        Assert.False(string.IsNullOrWhiteSpace(result.FormatName));
        Assert.True(result.BitRate > 0);
    }
}
