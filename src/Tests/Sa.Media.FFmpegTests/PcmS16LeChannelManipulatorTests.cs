using Sa.Media.FFmpeg;
using Sa.Media.FFmpeg.Services;

namespace Sa.Media.FFmpegTests;

public sealed class PcmS16LeChannelManipulatorTests : IDisposable
{
    private readonly TempDirectory _output = new();

    public void Dispose() => _output.Dispose();

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

        string outputPath = _output.Combine("output.wav_");

        // Act
        var result = await splitter.SplitAsync(
            inputPath, outputPath,
            outputSampleRate: 16000,
            isOverwrite: true,
            cancellationToken: CancellationToken);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.EndsWith("output_channel_0.wav_", result[0]);
        Assert.EndsWith("output_channel_1.wav_", result[1]);
        Assert.All(result, file => Assert.True(File.Exists(file), file));

        (int? channels, int? sampleRate) = await IFFProbeExecutor.Default.GetChannelsAndSampleRateAsync(result[0], CancellationToken);

        Assert.Equal(1, channels);
        Assert.Equal(16000, sampleRate);
    }


    [Fact]
    public async Task JoinAsync_CallsFFmpegWithJoinFilter()
    {
        // Arrange

        var manipulator = new PcmS16LeChannelManipulator();

        string outputPath = _output.Combine("stereo_join.wav_");

        // Act
        var result = await manipulator.JoinAsync(
            "./data/side_left.wav"
            , "./data/side_right.wav"
            , outputPath
            , isOverwrite: true
            , outputSampleRate: 16000
            , cancellationToken: CancellationToken);

        // Assert
        Assert.Equal(outputPath, result);
        Assert.True(File.Exists(result));

        (int? channels, int? sampleRate) =
            await IFFProbeExecutor.Default.GetChannelsAndSampleRateAsync(result, CancellationToken);

        Assert.Equal(2, channels);
        Assert.Equal(16000, sampleRate);
    }
}
