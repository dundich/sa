using Sa.Media.FFmpeg;
using Sa.Media.FFmpeg.Services;

namespace Sa.Media.FFmpegTests;

public sealed class PcmS16LeChannelSplitterTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(@".\data\input.mp3")]
    public async Task SplitAsync_WithValidInput_CreatesExpectedOutputFiles(string inputPath)
    {
        // Arrange
        var fakeFFMpeg = IFFMpegExecutor.Default;
        var fakeFFProbe = IFFProbeExecutor.Default;

        var splitter = new PcmS16LeChannelSplitter(fakeFFMpeg, fakeFFProbe);

        string outputPath = ".\\data\\output.wav_";

        // Act
        var result = await splitter.SplitAsync(
            inputPath, outputPath, 
            outputSampleRate: 16000,
            isOverwrite: true, 
            cancellationToken: CancellationToken);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains(".\\data\\output_channel_0.wav_", result);
        Assert.Contains(".\\data\\output_channel_1.wav_", result);
    }
}
