using Sa.Media.FFmpeg;

namespace Sa.Media.FFmpegTests;

public sealed class FFMpegProcessorTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    static IFFMpegExecutor Processor => IFFMpegExecutor.Default;


    [Fact]
    public async Task GetVersion_ShouldNotEmptyGetVersion()
    {
        // Act
        var r = await Processor.GetVersion(CancellationToken);
        Assert.NotEmpty(r);
    }

    [Fact]
    public async Task GetFormats_ShouldNotEmpty()
    {
        // Act
        var r = await Processor.GetFormats(CancellationToken);
        Assert.NotEmpty(r);
    }

    [Fact]
    public async Task GetCodecs_ShouldNotEmpty()
    {
        // Act
        var r = await Processor.GetCodecs(CancellationToken);
        Assert.NotEmpty(r);
    }

    [Theory]
    [InlineData("./data/input.mp3")]
    [InlineData("./data/gsm.wav")]
    public async Task ConvertToPcm16Wav_CallsFFmpegWithCorrectArguments(string testFilePath)
    {
        // Act
        var r = await Processor.ConvertToPcmS16Le(
            testFilePath, "./data/output.wav", isOverwrite: true, cancellationToken: CancellationToken);
        Assert.NotEmpty(r);
    }

    [Theory]
    [InlineData("./data/input.mp3")]
    [InlineData("./data/gsm.wav")]
    public async Task ConvertToPcm16Wav_CallsFFmpegAsStream(string testFilePath)
    {
        var fn = "./data/output.wav";

        var ext = Path.GetExtension(testFilePath).TrimStart('.');

        using var inputStream = File.OpenRead(testFilePath);

        // Act
        await Processor.ConvertToPcmS16Le(inputStream, ext, async outStream =>
        {
            using var fileStream = new FileStream(fn, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);

            await outStream.CopyToAsync(fileStream, CancellationToken);
            await fileStream.FlushAsync(CancellationToken);
        }, cancellationToken: CancellationToken);

        Assert.True(File.Exists(fn));
        File.Delete(fn);
    }


    [Theory]
    [InlineData("./data/input.ogg")]
    [InlineData("./data/input.wav")]
    public async Task ConvertToMp3_ShouldBeWork(string testFilePath)
    {
        // Act
        var r = await Processor.ConvertToMp3(
            testFilePath, "./data/output.mp3", isOverwrite: true, cancellationToken: CancellationToken);
        Assert.NotEmpty(r);
    }

    [Theory]
    [InlineData("./data/input.mp3")]
    [InlineData("./data/input.wav")]
    public async Task ConvertToOgg_ShouldBeWork(string testFilePath)
    {
        // Act
        var r = await Processor.ConvertToOgg(
            testFilePath, "./data/output.ogg_", isOverwrite: true, cancellationToken: CancellationToken);
        Assert.NotEmpty(r);
    }


    [Theory]
    [InlineData("./data/input.ogg")]
    [InlineData("./data/input.wav")]
    [InlineData("./data/input.mp3")]
    public async Task ConvertToMono_ShouldProduceValidMonoFile(string inputPath)
    {
        // Arrange
        string outputPath = Path.ChangeExtension(inputPath, ".pcm");

        if (File.Exists(outputPath))
            File.Delete(outputPath);


        var s = await Processor.ConvertToPcmS16Le(
            inputFileName: inputPath,
            outputFileName: outputPath,
            isOverwrite: true,
            outputChannelCount: 1,
            outputSampleRate: 8000,
            cancellationToken: CancellationToken
        );

        Assert.NotEmpty(s);

        Assert.True(File.Exists(outputPath));

        var ffprobe = CreateFFProbeExecutor();
        var (channels, sampleRate) = await ffprobe.GetChannelsAndSampleRate(outputPath, cancellationToken: CancellationToken);

        Assert.Equal(1, channels);
        Assert.Equal(8000, sampleRate);
    }

    private static IFFProbeExecutor CreateFFProbeExecutor()
    {
        return IFFProbeExecutor.Default;
    }
}
