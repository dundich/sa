using Sa.Classes;
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
        await Processor.ConvertToPcmS16Le(
            testFilePath, "./data/output.wav", isOverwrite: true, cancellationToken: CancellationToken);
        Assert.True(File.Exists("./data/output.wav"));
    }

    [Theory]
    [InlineData("./data/input.mp3")]
    [InlineData("./data/gsm.wav")]
    public async Task ConvertToPcm16Wav_CallsFFmpegAsStream(string testFilePath)
    {
        var ext = Path.GetExtension(testFilePath).TrimStart('.');

        using var inputStream = File.OpenRead(testFilePath);

        using var fileStream = new MemoryStream();
        // new FileStream("./data/output.wav", FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);

        // Act
        await Processor.ConvertToPcmS16Le(inputStream, ext, async (outStream, _) =>
        {
            await outStream.CopyToAsync(fileStream, CancellationToken);
            await fileStream.FlushAsync(CancellationToken);
        }, cancellationToken: CancellationToken);

        Assert.True(fileStream.Length > 0);

        fileStream.Position = 0;
        var info = await IFFProbeExecutor.Default.GetMetaInfo(fileStream, "wav", CancellationToken);
        Assert.Equal("wav", info.FormatName);
    }

    [Theory]
    [InlineData("./data/input.mp3")]
    public async Task ConvertToPcmS16Le_WhenFfmpegFails_ThrowsException(string testFilePath)
    {
        var ext = Path.GetExtension(testFilePath).TrimStart('.');

        using var inputStream = File.OpenRead(testFilePath);

        // Act
        var ex = await Assert.ThrowsAsync<ProcessExecutionException>(async () =>
        {
            await Processor.ConvertToPcmS16Le(inputStream, ext,
                (_, __) => Task.CompletedTask,
                cancellationToken: CancellationToken);
        });

        Assert.NotNull(ex);
    }


    [Theory]
    [InlineData("./data/input.mp3")]
    public async Task ConvertToPcmS16Le_WhenOutputCallbackThrows_ExceptionIsPropagated(string testFilePath)
    {
        var ext = Path.GetExtension(testFilePath).TrimStart('.');

        using var inputStream = File.OpenRead(testFilePath);

        var ex = await Assert.ThrowsAsync<Exception>(async () =>
        {
            await Processor.ConvertToPcmS16Le(inputStream, ext,
                (_, __) => throw new Exception("test"),
                cancellationToken: CancellationToken);
        });

        Assert.Equal("test", ex.Message);
    }


    [Fact]
    public async Task ConvertToPcmS16Le_WhenFmtInvalid_ExceptionIsPropagated()
    {
        await Assert.ThrowsAsync<ProcessExecutionException>(async () =>
        {
            await Processor.ConvertToPcmS16Le(new MemoryStream(),
                "wav",
                (_, __) => Task.CompletedTask,
                cancellationToken: CancellationToken);
        });
    }


    [Fact]
    public async Task ConvertToPcmS16Le_WhenCancelled_ThrowsOperationCanceledException()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var inputStream = new MemoryStream(new byte[1024]); // небольшой валидный поток (или имитация)
        await cts.CancelAsync(); // сразу отменяем — чтобы проверить реакцию

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await Processor.ConvertToPcmS16Le(
                inputStream: inputStream,
                inputFormat: "mp3",
                onOutput: (_, __) => Task.CompletedTask,
                cancellationToken: cts.Token);
        });
    }


    [Theory]
    [InlineData("./data/input.mp3")]
    public async Task ConvertToPcmS16Le_WhenCancelledDuringExecution_ThrowsOperationCanceledException(string testFilePath)
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        using var inputStream = File.OpenRead(testFilePath);

        var task = Processor.ConvertToPcmS16Le(
            inputStream: inputStream,
            inputFormat: "mp3",
            onOutput: async (output, ct) =>
            {
                var buffer = new byte[4096];
                try
                {
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        var read = await output.ReadAsync(buffer, ct);
                        if (read == 0) break;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
            },
            cancellationToken: cts.Token);

        // Отменяем через 100 мс
        await Task.Delay(100, CancellationToken);
        await cts.CancelAsync();

        // Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() => task);
    }


    [Theory]
    [InlineData("./data/input.ogg")]
    [InlineData("./data/input.wav")]
    public async Task ConvertToMp3_ShouldBeWork(string testFilePath)
    {
        var fn = "./data/output.mp3";
        // Act
        await Processor.ConvertToMp3(
            testFilePath, fn, isOverwrite: true, cancellationToken: CancellationToken);
        Assert.True(File.Exists(fn));
    }

    [Theory]
    [InlineData("./data/input.mp3")]
    [InlineData("./data/input.wav")]
    public async Task ConvertToOgg_ShouldBeWork(string testFilePath)
    {
        var fn = "./data/output.ogg_";
        // Act
        await Processor.ConvertToOgg(
            testFilePath, fn, isOverwrite: true, cancellationToken: CancellationToken);
        Assert.True(File.Exists(fn));
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

        await Processor.ConvertToPcmS16Le(
            inputFileName: inputPath,
            outputFileName: outputPath,
            isOverwrite: true,
            outputChannelCount: 1,
            outputSampleRate: 8000,
            cancellationToken: CancellationToken
        );

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
