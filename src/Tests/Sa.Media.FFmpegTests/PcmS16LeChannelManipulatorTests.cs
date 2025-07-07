using Sa.Media.FFmpeg;
using Sa.Media.FFmpeg.Services;

namespace Sa.Media.FFmpegTests;

public sealed class PcmS16LeChannelManipulatorTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("./data/input.mp3")]
    [InlineData("./data/stereo_join.wav")]
    public async Task SplitAsync_WithValidInput_CreatesExpectedOutputFiles(string inputPath)
    {
        // Arrange
        var fakeFFMpeg = IFFMpegExecutor.Default;
        var fakeFFProbe = IFFProbeExecutor.Default;

        var splitter = new PcmS16LeChannelManipulator(fakeFFMpeg, fakeFFProbe);

        string outputPath = "./data/output.wav_";

        // Act
        var result = await splitter.SplitAsync(
            inputPath, outputPath,
            outputSampleRate: 16000,
            isOverwrite: true,
            cancellationToken: CancellationToken);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains("./data/output_channel_0.wav_", result);
        Assert.Contains("./data/output_channel_1.wav_", result);

        (int? channels, int? sampleRate) = await IFFProbeExecutor.Default.GetChannelsAndSampleRate(result[0], CancellationToken);

        Assert.Equal(1, channels);
        Assert.Equal(16000, sampleRate);
    }


    [Fact]
    public async Task JoinAsync_CallsFFmpegWithJoinFilter()
    {
        // Arrange

        var manipulator = new PcmS16LeChannelManipulator();

        // Act
        var result = await manipulator.JoinAsync(
            "./data/side_left.wav"
            , "./data/side_right.wav"
            , "./data/stereo_join.wav_"
            , isOverwrite: true
            , outputSampleRate: 16000
            , cancellationToken: CancellationToken);

        // Assert
        Assert.Equal("./data/stereo_join.wav_", result);

        (int? channels, int? sampleRate) = await IFFProbeExecutor.Default.GetChannelsAndSampleRate(result, CancellationToken);

        Assert.Equal(2, channels);
        Assert.Equal(16000, sampleRate);
    }
}
