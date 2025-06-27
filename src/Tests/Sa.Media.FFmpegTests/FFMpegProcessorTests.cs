using Sa.Media.FFmpeg;

namespace Sa.Media.FFmpegTests;

public sealed class FFMpegProcessorTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public void Create_ShouldInitializeWithValidPath()
    {
        // Act
        var processor = FFMpegExecutor.Create();

        // Assert
        Assert.NotNull(processor);
        Assert.NotNull(processor.ExecutablePath);
    }

    [Fact]
    public async Task GetVersion_ShouldGetVersion()
    {
        // Act
        var processor = FFMpegExecutor.Create();
        var r = await processor.GetVersion(CancellationToken);
        Assert.NotEmpty(r);
    }
}
