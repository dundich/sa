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
        Assert.NotNull(processor.FFmpegExecutablePath);
    }

    [Fact]
    public async Task GetVersion_ShouldNotEmptyGetVersion()
    {
        // Act
        var processor = FFMpegExecutor.Create();
        var r = await processor.GetVersion(CancellationToken);
        Assert.NotEmpty(r);
    }

    [Fact]
    public async Task GetFormats_ShouldNotEmpty()
    {
        // Act
        var processor = FFMpegExecutor.Create();
        var r = await processor.GetFormats(CancellationToken);
        Assert.NotEmpty(r);
    }

    [Fact]
    public async Task GetCodecs_ShouldNotEmpty()
    {
        // Act
        var processor = FFMpegExecutor.Create();
        var r = await processor.GetCodecs(CancellationToken);
        Assert.NotEmpty(r);
    }

    [Fact]
    public async Task ConvertToPcm16Wav_CallsFFmpegWithCorrectArguments()
    {
        // Act
        var processor = FFMpegExecutor.Create();
        var r = await processor.ConvertToPcmS16Le(
            @".\data\input.mp3", @".\data\output.wav_", isOverwrite: true, cancellationToken: CancellationToken);
        Assert.NotEmpty(r);
    }

    [Fact]
    public async Task GetAudioChannelCount_ShouldBeWork()
    {
        // Act
        var processor = FFMpegExecutor.Create();
        var r = await processor.GetAudioChannelCount(@".\data\input.mp3", cancellationToken: CancellationToken);
        Assert.Equal(2, r);
    }

    [Fact]
    public async Task ConvertToMp3_ShouldBeWork()
    {
        // Act
        var processor = FFMpegExecutor.Create();
        var r = await processor.ConvertToMp3(
            @".\data\input.wav", @".\data\output.mp3_", isOverwrite: true, cancellationToken: CancellationToken);
        Assert.NotEmpty(r);
    }

    [Fact]
    public async Task ConvertToOgg_ShouldBeWork()
    {
        // Act
        var processor = FFMpegExecutor.Create();
        var r = await processor.ConvertToOgg(
            @".\data\input.wav", @".\data\output.ogg_", isOverwrite: true, cancellationToken: CancellationToken);
        Assert.NotEmpty(r);
    }
}
