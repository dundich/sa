using Sa.Media.FFmpeg;

namespace Sa.Media.FFmpegTests;

public sealed class FFProbeProcessorTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    static IFFProbeExecutor Processor => IFFProbeExecutor.Default;

    [Fact]
    public async Task GetAudioChannelCount_ShouldBeWork()
    {
        // Act
        var r = await Processor.GetAudioChannelCount(@".\data\input.mp3", cancellationToken: CancellationToken);
        Assert.Equal(2, r);
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
